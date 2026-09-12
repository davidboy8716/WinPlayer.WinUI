using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace WinPlayer.WinUI.Services;

/// <summary>mpv 轨道信息（音轨/字幕轨）。</summary>
/// <param name="Kind">轨道类型：audio / sub / video。</param>
/// <param name="Id">mpv 内部轨道 id，用于设置 aid/sid。</param>
/// <param name="Title">轨道标题（标题不存在时为空）。</param>
/// <param name="Language">轨道语言。</param>
public readonly record struct MpvTrackInfo(string Kind, int Id, string Title, string Language);

/// <summary>
/// 基于 libmpv 的杜比视界播放引擎（方案 A：软件渲染 API + libplacebo 滤镜链）。
///
/// 背景：Windows 媒体框架不处理杜比视界 Profile 5 的 IPT/RPU，画面必然发绿/发紫；
/// libmpv 的软件渲染器本身同样不处理（实测结果等同未处理），
/// 但加上 <c>vf=libplacebo</c> 滤镜后由 libplacebo/Vulkan 完成杜比视界重塑与 HDR→SDR 转换，
/// 颜色正确且每帧只需约 2.4 ms（1920×804，实测）。
///
/// 本类不依赖 Win2D/XAML：渲染结果放在 <see cref="FrameBuffer"/>（BGRA，自上而下），
/// 由界面层读取并上传到画布。请在后台线程使用事件回调。
/// </summary>
public sealed class MpvDvEngine : IDisposable
{
    private const string LibraryName = "libmpv-2.dll";

    private static readonly object ResolverLock = new();
    private static bool resolverInstalled;

    // ---- 状态与事件 ----

    private IntPtr handle;
    private IntPtr renderContext;
    private Thread? renderThread;
    private readonly ManualResetEventSlim renderThreadExited = new(false);
    private ManualResetEventSlim? renderSignal;
    private volatile bool running;
    private IntPtr pixelBuffer;
    private int pixelBufferCapacity;
    private readonly object frameLock = new();
    private byte[] frameBuffer = Array.Empty<byte>();
    private int frameWidth;
    private int frameHeight;
    private int frameStride;
    private int videoWidth;
    private int videoHeight;
    private long renderedFrames;
    private double lastRenderMilliseconds;

    /// <summary>日志通道；由宿主决定写到哪里。</summary>
    public Action<string>? Trace { get; set; }

    /// <summary>新帧已写入 <see cref="FrameBuffer"/>（可能在渲染线程触发，宿主需自行切换线程）。</summary>
    public event Action? FrameAvailable;

    /// <summary>视频尺寸或输出缓冲发生变化。</summary>
    public event Action<int, int>? VideoSizeChanged;

    /// <summary>媒体加载完成（可以读取 Duration 等属性）。</summary>
    public event Action? FileLoaded;

    /// <summary>播放到结尾或文件被卸载。</summary>
    public event Action? EndReached;

    /// <summary>已渲染帧数。</summary>
    public long RenderedFrames => Interlocked.Read(ref renderedFrames);

    /// <summary>最近一帧的渲染耗时（毫秒）。</summary>
    public double LastRenderMilliseconds => lastRenderMilliseconds;

    public bool IsRunning => running;
    public bool HasMedia { get; private set; }

    /// <summary>BGRA 像素缓冲（自上而下，行距 <see cref="FrameStride"/>）。读取前请用 <see cref="CopyFrame"/>。</summary>
    public byte[] FrameBuffer => frameBuffer;
    public int FrameWidth => frameWidth;
    public int FrameHeight => frameHeight;
    public int FrameStride => frameStride;

    /// <summary>
    /// 解码后视频的原始显示尺寸（mpv 的 dwidth/dheight，未按输出上限缩放）。
    /// 供窗口几何等只依赖宽高比的场景使用；<see cref="FrameWidth"/>/<see cref="FrameHeight"/>
    /// 是受 1920 宽度上限和偶数对齐约束的输出缓冲尺寸。
    /// </summary>
    public int VideoWidth => videoWidth;
    public int VideoHeight => videoHeight;

    // ---- 生命周期 ----

    public MpvDvEngine(string? libraryPath = null)
    {
        EnsureLibraryResolved(libraryPath);
        handle = LibMpv.mpv_create();
        if (handle == IntPtr.Zero) throw new InvalidOperationException("mpv_create 失败：无法创建 libmpv 实例");
        IsAvailable = true;
    }

    public bool IsAvailable { get; private set; }

    /// <summary>创建并初始化引擎；失败时返回 null 并给出原因，调用方降级到内置播放器。</summary>
    public static MpvDvEngine? TryCreate(out string? error, string? libraryPath = null)
    {
        try
        {
            var engine = new MpvDvEngine(libraryPath);
            engine.Initialize();
            error = null;
            return engine;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private void Initialize()
    {
        // 必须使用 libmpv 渲染 API；-vf libplacebo 负责杜比视界重塑与 HDR→SDR 色调映射。
        SetOption("vo", "libmpv");
        SetOption("config", "no");
        SetOption("idle", "yes");
        SetOption("terminal", "no");
        SetOption("input-default-bindings", "no");
        SetOption("input-vo-keyboard", "no");
        SetOption("osc", "no");
        SetOption("osd-level", "1");
        SetOption("hwdec", "auto-safe");
        SetOption("cache", "yes");
        SetOption("demuxer-max-bytes", "256MiB");
        SetOption("demuxer-readahead-secs", "20");
        SetOption("force-window", "no");
        SetOption("keep-open", "no");
        SetOption("sub-auto", "fuzzy");
        // 只渲染一条字幕：副字幕默认关闭，避免同一句字幕（简繁两轨内容相同时）出现两行。
        SetOption("secondary-sid", "no");
        SetOption("secondary-sub-visibility", "no");
        SetOption("msg-level", "all=warn");
        SetOption("vf", "libplacebo=color_primaries=bt709:color_trc=bt709:colorspace=bt709:range=tv");
        SetOption("target-trc", "srgb");
        SetOption("target-prim", "bt.709");
        SetOption("target-peak", "200");

        int rc = LibMpv.mpv_initialize(handle);
        if (rc < 0) throw new InvalidOperationException($"mpv_initialize 失败：{LibMpv.ErrorString(rc)}");

        renderSignal = new ManualResetEventSlim(false);
        CreateRenderContext();

        running = true;
        renderThread = new Thread(RenderLoop)
        {
            IsBackground = true,
            Name = "mpv-render"
        };
        renderThread.Start();
        Trace?.Invoke($"libmpv 引擎已启动（{LibMpv.ResolvedPath}）");
    }

    private void CreateRenderContext()
    {
        IntPtr apiType = Marshal.StringToCoTaskMemUTF8(LibMpv.MPV_RENDER_API_TYPE_SW);
        var param = new LibMpv.MpvRenderParam { type = LibMpv.MPV_RENDER_PARAM_API_TYPE, data = apiType };
        IntPtr paramArray = Marshal.AllocHGlobal(Marshal.SizeOf<LibMpv.MpvRenderParam>());
        try
        {
            Marshal.StructureToPtr(param, paramArray, false);
            int rc = LibMpv.mpv_render_context_create(out renderContext, handle, paramArray);
            if (rc < 0) throw new InvalidOperationException($"mpv_render_context_create 失败：{LibMpv.ErrorString(rc)}");
        }
        finally
        {
            Marshal.FreeHGlobal(paramArray);
            Marshal.FreeCoTaskMem(apiType);
        }

        updateCallback = _ => renderSignal?.Set();
        LibMpv.mpv_render_context_set_update_callback(renderContext, updateCallback, IntPtr.Zero);
    }

    private LibMpv.MpvRenderUpdateFn? updateCallback;

    // ---- 渲染循环 ----

    private void RenderLoop()
    {
        try
        {
            while (running)
            {
                // 事件必须在同一线程读取，避免与 render API 争用。
                PumpEvents();
                renderSignal?.Wait(16);
                renderSignal?.Reset();

                if (!running) break;
                ulong flags = LibMpv.mpv_render_context_update(renderContext);
                if ((flags & LibMpv.MPV_RENDER_UPDATE_FRAME) == 0) continue;
                RenderFrame();
            }
        }
        catch (Exception ex)
        {
            Trace?.Invoke("渲染线程异常：" + ex.Message);
        }
        finally
        {
            // mpv 的渲染上下文与实例必须由渲染线程自己销毁：
            // 在渲染调用进行中从别的线程销毁会争用，表现为关闭窗口时卡死（无法退出程序）。
            if (renderContext != IntPtr.Zero)
            {
                LibMpv.mpv_render_context_free(renderContext);
                renderContext = IntPtr.Zero;
            }
            if (handle != IntPtr.Zero)
            {
                LibMpv.mpv_terminate_destroy(handle);
                handle = IntPtr.Zero;
            }
            renderThreadExited.Set();
        }
    }

    private void PumpEvents()
    {
        while (true)
        {
            IntPtr evt = LibMpv.mpv_wait_event(handle, 0);
            if (evt == IntPtr.Zero) return;
            var e = Marshal.PtrToStructure<LibMpv.MpvEvent>(evt);
            if (e.event_id == LibMpv.MPV_EVENT_NONE) return;

            switch (e.event_id)
            {
                case LibMpv.MPV_EVENT_FILE_LOADED:
                    HasMedia = true;
                    FileLoaded?.Invoke();
                    break;
                case LibMpv.MPV_EVENT_VIDEO_RECONFIG:
                    UpdateOutputSize();
                    break;
                case LibMpv.MPV_EVENT_END_FILE:
                    EndReached?.Invoke();
                    break;
            }
        }
    }

    /// <summary>按视频显示尺寸计算输出缓冲大小（最长边不超过 1920）。</summary>
    private void UpdateOutputSize()
    {
        int width = (int)(LibMpv.GetInt64(handle, "dwidth") ?? 0);
        int height = (int)(LibMpv.GetInt64(handle, "dheight") ?? 0);
        if (width <= 0 || height <= 0) return;

        // 原始尺寸必须在按输出上限缩放之前记录：窗口几何只依赖宽高比，
        // 而下面的 frameWidth/frameHeight 是为像素缓冲裁剪与对齐后的输出尺寸。
        videoWidth = width;
        videoHeight = height;

        const int maxWidth = 1920;
        if (width > maxWidth)
        {
            height = Math.Max(2, (int)Math.Round(height * (double)maxWidth / width));
            width = maxWidth;
        }
        width &= ~1;
        height &= ~1;
        if (width == frameWidth && height == frameHeight) return;

        lock (frameLock)
        {
            frameWidth = width;
            frameHeight = height;
            frameStride = width * 4;
            int needed = frameStride * height;
            if (frameBuffer.Length != needed) frameBuffer = new byte[needed];
            EnsurePixelBuffer(needed);
        }
        Trace?.Invoke($"视频输出尺寸：{width}×{height}");
        VideoSizeChanged?.Invoke(width, height);
    }

    private void EnsurePixelBuffer(int bytes)
    {
        if (pixelBuffer != IntPtr.Zero && pixelBufferCapacity >= bytes) return;
        if (pixelBuffer != IntPtr.Zero) Marshal.FreeHGlobal(pixelBuffer);
        pixelBuffer = Marshal.AllocHGlobal(bytes);
        pixelBufferCapacity = bytes;
    }

    private void RenderFrame()
    {
        int width, height, stride;
        IntPtr target;
        lock (frameLock)
        {
            if (pixelBuffer == IntPtr.Zero || frameWidth <= 0) return;
            width = frameWidth;
            height = frameHeight;
            stride = frameStride;
            target = pixelBuffer;
        }

        long start = Environment.TickCount64;
        int rc = RenderToBuffer(target, width, height, stride);
        lastRenderMilliseconds = Environment.TickCount64 - start;
        if (rc < 0)
        {
            if (rc != LibMpv.MPV_ERROR_UNINITIALIZED)
                Trace?.Invoke($"渲染失败：{LibMpv.ErrorString(rc)}");
            return;
        }

        lock (frameLock)
        {
            if (frameBuffer.Length == stride * height)
                Marshal.Copy(target, frameBuffer, 0, frameBuffer.Length);
        }
        Interlocked.Increment(ref renderedFrames);
        FrameAvailable?.Invoke();
    }

    private int RenderToBuffer(IntPtr target, int width, int height, int stride)
    {
        IntPtr sizePtr = Marshal.AllocHGlobal(8);
        IntPtr formatPtr = Marshal.StringToCoTaskMemUTF8("bgra");
        IntPtr stridePtr = Marshal.AllocHGlobal(IntPtr.Size);
        var parameters = new LibMpv.MpvRenderParam[5];
        IntPtr arrayPtr = Marshal.AllocHGlobal(Marshal.SizeOf<LibMpv.MpvRenderParam>() * parameters.Length);
        try
        {
            Marshal.WriteInt32(sizePtr, width);
            Marshal.WriteInt32(sizePtr, 4, height);
            Marshal.WriteIntPtr(stridePtr, stride);

            parameters[0] = new LibMpv.MpvRenderParam { type = LibMpv.MPV_RENDER_PARAM_SW_SIZE, data = sizePtr };
            parameters[1] = new LibMpv.MpvRenderParam { type = LibMpv.MPV_RENDER_PARAM_SW_FORMAT, data = formatPtr };
            parameters[2] = new LibMpv.MpvRenderParam { type = LibMpv.MPV_RENDER_PARAM_SW_STRIDE, data = stridePtr };
            // SW_POINTER 直接传像素缓冲地址本身（不是指向地址的指针）。
            parameters[3] = new LibMpv.MpvRenderParam { type = LibMpv.MPV_RENDER_PARAM_SW_POINTER, data = target };
            parameters[4] = new LibMpv.MpvRenderParam { type = LibMpv.MPV_RENDER_PARAM_INVALID, data = IntPtr.Zero };

            for (int i = 0; i < parameters.Length; i++)
                Marshal.StructureToPtr(parameters[i], arrayPtr + i * Marshal.SizeOf<LibMpv.MpvRenderParam>(), false);

            return LibMpv.mpv_render_context_render(renderContext, arrayPtr);
        }
        finally
        {
            Marshal.FreeHGlobal(arrayPtr);
            Marshal.FreeHGlobal(sizePtr);
            Marshal.FreeCoTaskMem(formatPtr);
            Marshal.FreeHGlobal(stridePtr);
        }
    }

    /// <summary>把当前帧复制到调用方缓冲，返回是否复制成功。</summary>
    public bool CopyFrame(byte[] destination, out int width, out int height, out int stride)
    {
        lock (frameLock)
        {
            width = frameWidth;
            height = frameHeight;
            stride = frameStride;
            if (frameBuffer.Length == 0 || destination.Length < frameBuffer.Length) return false;
            Buffer.BlockCopy(frameBuffer, 0, destination, 0, frameBuffer.Length);
            return true;
        }
    }

    // ---- 播放控制 ----

    /// <summary>
    /// 加载媒体。起始位置通过 mpv 的 <c>start</c> 文件级选项设置：
    /// loadfile 之后立刻 seek 会因为文件尚未开始播放而失败（MPV_ERROR_COMMAND）。
    /// </summary>
    public void Load(string path, double startSeconds = 0)
    {
        if (startSeconds > 1)
        {
            SetProperty("start", startSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        }
        Check(LibMpv.Command(handle, "loadfile", path, "replace"), "loadfile");
    }

    public void Play() => SetProperty("pause", "no");
    public void Pause() => SetProperty("pause", "yes");
    public bool IsPaused => string.Equals(LibMpv.GetString(handle, "pause"), "yes", StringComparison.OrdinalIgnoreCase);

    public void Seek(double seconds) =>
        Check(LibMpv.Command(handle, "seek", seconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
            "absolute"), "seek");

    public void SeekRelative(double seconds) =>
        Check(LibMpv.Command(handle, "seek", seconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
            "relative"), "seek");

    public void Stop() => Check(LibMpv.Command(handle, "stop"), "stop");

    public void SetSpeed(double speed) =>
        SetProperty("speed", speed.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>音量按 0–1 传入，映射到 mpv 的 0–100。</summary>
    public void SetVolume(double volume0To1)
    {
        double percent = Math.Clamp(volume0To1, 0, 1) * 100;
        SetProperty("volume", percent.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
    }

    public void SetMuted(bool muted) => SetProperty("mute", muted ? "yes" : "no");

    public double Position => LibMpv.GetDouble(handle, "time-pos") ?? 0;
    public double Duration => LibMpv.GetDouble(handle, "duration") ?? 0;
    public bool IsPlaying => !IsPaused && HasMedia;

    public void SetAudioTrack(int id) => SetProperty("aid", id > 0 ? id.ToString() : "no");

    /// <summary>id ≤ 0 表示关闭字幕。</summary>
    public void SetSubtitleTrack(int id) => SetProperty("sid", id > 0 ? id.ToString() : "no");

    /// <summary>
    /// 字幕是否被明确关闭（<c>sid=no</c>）。
    /// 与"暂时读不到当前字幕轨"不同：引擎自动选择时可能读不到当前轨，
    /// 不能据此认为字幕已关闭。
    /// </summary>
    public bool SubtitlesExplicitlyOff =>
        string.Equals(LibMpv.GetString(handle, "sid"), "no", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 当前正在播放的字幕轨 id；未选择字幕时为 0。
    /// 注意 <c>sid</c> 属性是字符串（可能是 "auto"），因此优先读 <c>current-tracks/sub/id</c>。
    /// </summary>
    public int CurrentSubtitleTrackId
    {
        get
        {
            long? current = LibMpv.GetInt64(handle, "current-tracks/sub/id");
            if (current is long id && id > 0) return (int)id;
            string? sid = LibMpv.GetString(handle, "sid");
            return int.TryParse(sid, out int parsed) ? parsed : 0;
        }
    }

    /// <summary>读取 mpv 的轨道列表（音轨/字幕轨），用于填充界面菜单。</summary>
    public IReadOnlyList<MpvTrackInfo> GetTracks()
    {
        var result = new List<MpvTrackInfo>();
        int count = (int)(LibMpv.GetInt64(handle, "track-list/count") ?? 0);
        for (int i = 0; i < count; i++)
        {
            string kind = LibMpv.GetString(handle, $"track-list/{i}/type") ?? string.Empty;
            long id = LibMpv.GetInt64(handle, $"track-list/{i}/id") ?? 0;
            string title = LibMpv.GetString(handle, $"track-list/{i}/title") ?? string.Empty;
            string language = LibMpv.GetString(handle, $"track-list/{i}/lang") ?? string.Empty;
            result.Add(new MpvTrackInfo(kind, (int)id, title, language));
        }
        return result;
    }

    private void SetOption(string name, string value) =>
        Check(LibMpv.mpv_set_option_string(handle, name, value), $"option {name}={value}");

    private void SetProperty(string name, string value) =>
        Check(LibMpv.mpv_set_property_string(handle, name, value), $"property {name}={value}");

    private static void Check(int result, string what)
    {
        if (result < 0) throw new InvalidOperationException($"{what} 失败：{LibMpv.ErrorString(result)}（{result}）");
    }

    // ---- 资源释放 ----

    public void Dispose()
    {
        running = false;
        renderSignal?.Set();

        // 等待渲染线程自行销毁 mpv 资源；超时则不再等待，交由进程退出时由系统回收，
        // 绝不能让关闭流程卡在这里。
        bool exited = false;
        try
        {
            exited = renderThreadExited.Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
            // 忽略等待异常，按未结束处理。
        }

        if (!exited)
        {
            Trace?.Invoke("渲染线程未在 5 秒内结束，跳过同步销毁以免阻塞退出");
            IsAvailable = false;
            return;
        }

        if (pixelBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(pixelBuffer);
            pixelBuffer = IntPtr.Zero;
            pixelBufferCapacity = 0;
        }
        renderSignal?.Dispose();
        renderSignal = null;
        try
        {
            renderThreadExited.Dispose();
        }
        catch (Exception)
        {
            // 已释放或正在释放时忽略。
        }
        IsAvailable = false;
    }

    // ---- 动态库定位 ----

    /// <summary>
    /// 定位 libmpv-2.dll：优先环境变量 <c>WINPLAYER_LIBMPV</c>，其次程序目录，
    /// 最后向上查找开发目录 <c>.tools/mpv-dev</c>。找到后注册 DllImport 解析器。
    /// </summary>
    public static string? LocateLibrary(string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
            return Path.GetFullPath(explicitPath);

        string? env = Environment.GetEnvironmentVariable("WINPLAYER_LIBMPV");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return Path.GetFullPath(env);

        string[] relative =
        {
            LibraryName,
            Path.Combine("libmpv", LibraryName),
            Path.Combine("runtimes", "win-x64", "native", LibraryName)
        };
        foreach (string candidate in relative)
        {
            string path = Path.Combine(AppContext.BaseDirectory, candidate);
            if (File.Exists(path)) return path;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            foreach (string probe in new[]
            {
                Path.Combine(directory.FullName, ".tools", "mpv-dev", LibraryName),
                Path.Combine(directory.FullName, "mpv-dev", LibraryName)
            })
            {
                if (File.Exists(probe)) return probe;
            }
            directory = directory.Parent;
        }
        return null;
    }

    private static void EnsureLibraryResolved(string? explicitPath)
    {
        lock (ResolverLock)
        {
            string? path = LocateLibrary(explicitPath)
                ?? throw new FileNotFoundException(
                    $"{LibraryName} 未找到：请把它放到程序目录（或设置 WINPLAYER_LIBMPV 环境变量）");
            LibMpv.ResolvedPath = path;
            if (resolverInstalled) return;
            NativeLibrary.SetDllImportResolver(typeof(MpvDvEngine).Assembly, (name, assembly, searchPath) =>
                name == LibraryName && LibMpv.ResolvedPath is not null
                    ? NativeLibrary.Load(LibMpv.ResolvedPath)
                    : IntPtr.Zero);
            resolverInstalled = true;
        }
    }
}
