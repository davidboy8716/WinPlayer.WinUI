using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Windows.Graphics;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI.WindowManagement;
using WinPlayer.WinUI.Models;
using WinPlayer.WinUI.Mvvm;
using WinPlayer.WinUI.Services;
using WinRT.Interop;

namespace WinPlayer.WinUI.ViewModels;

/// <summary>
/// 管理播放状态、命令、媒体/文件夹集合以及持久化流程。
/// 窗口移动和面板动画等纯界面逻辑仍由
/// <see cref="WinPlayer.WinUI.MainWindow"/> 负责。
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    public event EventHandler<UserNotificationEventArgs>? NotificationRequested;
    public event Action<PgsSubtitleImage?>? PgsSubtitleFrameChanged;

    private static readonly HashSet<string> SupportedMediaExtensions = SupportedMedia.MediaExtensionSet;
    private static readonly HashSet<string> SupportedSubtitleExtensions = SupportedMedia.SubtitleExtensionSet;
    private readonly Func<Task<IReadOnlyList<StorageFile>>> pickMediaFiles;
    private readonly Action toggleFullScreen;
    private readonly Action enterFullScreen;
    private readonly Func<Task> openSettings;
    private readonly Action persistSettings;
    private readonly DispatcherQueue dispatcherQueue;
    // 播放进度和控制层自动隐藏计时器会修改绑定状态，因此在 UI 调度器上运行。
    private readonly DispatcherTimer positionTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer chromeTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly PlaybackHistoryService playbackHistory;
    private readonly PlaylistPersistenceService playlistPersistence = new();
    private CancellationTokenSource? playlistHistoryResolutionCancellation;
    private DateTime lastHistoryWriteUtc;
    private bool playbackSessionUnavailableLogged;
    private PlaylistItem? currentItem;
    private double pendingResumePosition;
    private bool isSkippingOutro;
    private bool forcePlayOnMediaOpened;
    private bool ignoreResumeOnce;
    private string? historySuppressedForPath;
    // 每个媒体源都有独立的版本号，旧媒体源被取消的音量渐变不能修改新媒体源的音量。
    private CancellationTokenSource? volumeFadeCancellation;
    private long mediaGeneration;
    private bool isVolumeFading;
    private bool pendingVolumeFadeIn;
    private MediaPlaybackItem? playbackItem;
    private MediaSource? activeMediaSource;
    private readonly HashSet<string> loadedExternalSubtitlePaths = new(StringComparer.OrdinalIgnoreCase);
    // TimedTextSource 不接管调用方打开的流；播放期间必须保持流有效。
    private readonly List<IRandomAccessStream> externalSubtitleStreams = new();
    private readonly Dictionary<TimedMetadataTrack, PgsSubtitleDocument> pgsSubtitleDocuments = new();
    private CancellationTokenSource? pgsDecodeCancellation;
    private PgsSubtitleDocument? activePgsSubtitleDocument;
    private int activePgsFrameIndex = -2;
    private bool hasLoggedFirstPgsFrame;
    private TimedMetadataTrack? activeSubtitleTrack;
    private string subtitleText = string.Empty;
    private string title = "本地资源播放器";
    private string currentTime = "00:00";
    private string durationText = "00:00";
    private string playPauseGlyph = "▶";
    private double position;
    private double duration = 1;
    private double volume = 80;
    private double mediaWidth = 0;
    private double mediaHeight = 0;
    private double volumeBeforeMute = 80;
    private double chromeOpacity = 1;
    private Visibility emptyStateVisibility = Visibility.Visible;
    private Visibility pausedOverlayVisibility = Visibility.Collapsed;
    private bool isSeeking;
    // 实际发声引擎的播放状态。系统媒体框架与 libmpv 引擎互斥：引擎播放时系统会话
    // 没有媒体源，直接读它的 PlaybackState 会得到"未播放"，因此状态统一记录在这里。
    private bool isPlaying;
    private bool isLoopingEnabled;
    private int loadingOperationCount;
    private bool isLoading;
    private string loadingMessage = "正在加载…";
    private PlaylistItem? selectedItem;
    private double leftSidebarWidth;
    private double rightSidebarWidth;

    public MainViewModel(Func<Task<IReadOnlyList<StorageFile>>> pickMediaFiles, Action toggleFullScreen,
        Action enterFullScreen, Action toggleFolders, Action togglePlaylist,
        DispatcherQueue dispatcherQueue, PlayerSettings settings,
        Func<Task> openSettings, Action persistSettings)
    {
        this.pickMediaFiles = pickMediaFiles;
        this.toggleFullScreen = toggleFullScreen;
        this.enterFullScreen = enterFullScreen;
        this.openSettings = openSettings;
        this.persistSettings = persistSettings;
        this.dispatcherQueue = dispatcherQueue;
        Settings = settings;
        // 记录服务按进程模式选择文件：普通模式 playback-history.json，隐私模式 history.b.json。
        // 隐私模式下两者互不写入，因此隐私播放对普通模式零影响。
        playbackHistory = new PlaybackHistoryService(AppMode.IsPrivacy);
        // 帧服务器模式用于把解码帧交给 Win2D 自绘；HDR/杜比直通模式则把画面交回系统合成器，
        // 以便系统按显示器的 HDR 能力输出（自绘路径拿到的是 8bit SDR 表面）。
        MediaPlayer.IsVideoFrameServerEnabled = !settings.HdrDirectPresent;
        volume = settings.Volume;
        volumeBeforeMute = settings.Volume > 0 ? settings.Volume : 80;
        MediaPlayer.Volume = settings.Volume / 100d;
        MediaPlayer.MediaOpened += MediaPlayer_MediaOpened;
        MediaPlayer.MediaEnded += MediaPlayer_MediaEnded;
        MediaPlayer.MediaFailed += MediaPlayer_MediaFailed;
        MediaPlayer.PlaybackSession.PlaybackStateChanged += PlaybackSession_PlaybackStateChanged;
        positionTimer.Tick += PositionTimer_Tick;
        chromeTimer.Tick += ChromeTimer_Tick;
        positionTimer.Start();
        OpenFileCommand = new AsyncRelayCommand(OpenFileAsync);
        PlayPauseCommand = new RelayCommand(PlayPause);
        BackCommand = new RelayCommand(() => SeekBy(TimeSpan.FromSeconds(-Settings.SeekSeconds)));
        ForwardCommand = new RelayCommand(() => SeekBy(TimeSpan.FromSeconds(Settings.SeekSeconds)));
        PreviousCommand = new RelayCommand(PlayPrevious);
        NextCommand = new RelayCommand(PlayNext);
        StopPlaybackCommand = new RelayCommand(StopPlayback);
        ToggleFoldersCommand = new RelayCommand(toggleFolders);
        TogglePlaylistCommand = new RelayCommand(togglePlaylist);
        FullScreenCommand = new RelayCommand(() => { toggleFullScreen(); ShowChrome(); });
        SettingsCommand = new AsyncRelayCommand(openSettings);
        ToggleMuteCommand = new RelayCommand(ToggleMute);
        SetIntroMarkerCommand = new RelayCommand(ToggleIntroMarker);
        SetOutroMarkerCommand = new RelayCommand(ToggleOutroMarker);
        ShowChrome();
    }

    public MediaPlayer MediaPlayer { get; } = new();
    /// <summary>当前媒体的文件路径；未打开媒体时为 <c>null</c>。供 HDR/杜比诊断使用。</summary>
    public string? CurrentMediaPath => currentItem?.Path;    /// <summary>当前媒体播放项；视频轨道编码信息由它提供。供 HDR/杜比诊断使用。</summary>
    public MediaPlaybackItem? PlaybackItem => playbackItem;
    /// <summary>是否存在正在播放（或已打开）的媒体。</summary>
    public bool HasCurrentMedia => currentItem is not null;

    /// <summary>
    /// 按当前设置应用“帧服务器自绘 / 系统直通呈现”。
    /// 直通模式用于验证 HDR/杜比内容能否借助系统管线正确输出。
    /// </summary>
    public void ApplyRenderModeSetting()
    {
        bool desiredFrameServer = !Settings.HdrDirectPresent;
        if (MediaPlayer.IsVideoFrameServerEnabled == desiredFrameServer) return;
        try
        {
            MediaPlayer.IsVideoFrameServerEnabled = desiredFrameServer;
        }
        catch (Exception ex)
        {
            AppLogService.Warning("RenderModeSwitchFailed", "切换视频呈现模式失败",
                new { DirectPresent = Settings.HdrDirectPresent }, ex);
        }
    }
    public PlayerSettings Settings { get; }

    // ------------------------------------------------------------------
    // libmpv 杜比视界引擎（方案 A：SW 渲染 API + libplacebo 滤镜链）
    // 系统媒体框架不处理杜比视界 Profile 5 的 IPT/RPU（画面发绿/发紫），
    // 该引擎用 libmpv 渲染正确的画面，再由界面层上传到 Win2D 画布显示。
    // ------------------------------------------------------------------

    private MpvDvEngine? mpvEngine;
    private int mpvFramePending;
    // 引擎字幕轨在界面菜单中的顺序与实际 mpv 轨道的对应关系（用于切换与记住选择）。
    private readonly List<MpvTrackInfo> mpvSubtitleTracks = new();

    /// <summary>libmpv-2.dll 是否存在（不存在时该引擎不可用，功能自动降级）。</summary>
    public bool IsMpvEngineAvailable { get; } = MpvDvEngine.LocateLibrary() is not null;

    /// <summary>当前是否正由 libmpv 引擎播放。</summary>
    public bool IsMpvEngineActive => mpvEngine is not null && mpvEngine.HasMedia;

    /// <summary>当前 libmpv 引擎实例（未启用时为 null）。</summary>
    public MpvDvEngine? MpvEngine => mpvEngine;

    /// <summary>libmpv 渲染出新帧（在渲染线程触发；界面层需切回 UI 线程再绘制，并调用 MarkMpvFrameConsumed）。</summary>
    public event Action? MpvFrameAvailable;

    /// <summary>界面层已消费该帧，允许下一次通知。</summary>
    public void MarkMpvFrameConsumed() => Interlocked.Exchange(ref mpvFramePending, 0);

    /// <summary>用 libmpv 引擎播放指定媒体。</summary>
    public bool StartMpvPlayback(PlaylistItem item, double startSeconds = 0)
    {
        if (MpvDvEngine.TryCreate(out string? error) is not { } engine)
        {
            AppLogService.Warning("MpvEngineUnavailable", "libmpv 引擎不可用", new { Error = error });
            Notify($"libmpv 引擎不可用：{error}", true);
            return false;
        }

        StopMpvPlayback();
        mpvEngine = engine;
        engine.Trace = message => AppLogService.Information("MpvEngine", message);
        engine.FrameAvailable += MpvEngine_FrameAvailable;
        engine.EndReached += MpvEngine_EndReached;
        engine.FileLoaded += MpvEngine_FileLoaded;

        currentItem = item;
        Title = item.Name;
        EmptyStateVisibility = Visibility.Collapsed;
        OnPropertyChanged(nameof(IsMediaOpen));
        // 引擎模式下字幕完全由 libmpv 渲染，本应用的字幕图层必须退出，
        // 否则同一句字幕会被画两次（系统会话的字幕浮层 / 自绘图片字幕 + mpv 字幕）。
        DetachSubtitleTrack();
        ClearExternalSubtitleState();
        SubtitleText = string.Empty;
        PgsSubtitleFrameChanged?.Invoke(null);
        // 音量渐入在引擎模式下同样要生效：系统播放器的音量与引擎无关，
        // 因此这里从 0 起播，再由 StartPendingVolumeFade 逐步升到目标音量。
        pendingVolumeFadeIn = Settings.EnableVolumeFadeIn && Settings.VolumeFadeInDuration > 0 && Volume > 0;
        engine.SetVolume(pendingVolumeFadeIn ? 0 : Volume / 100d);
        engine.SetSpeed(1);
        // 记住的“关闭字幕”在装载前就生效，避免先闪出片源默认字幕。
        if (Settings.EngineSubtitlesOff) engine.SetSubtitleTrack(0);
        engine.Load(item.Path, startSeconds);
        engine.Play();
        IsPlaying = true;
        StartPendingVolumeFade();
        ShowChrome();
        Notify("已启用 libmpv 杜比视界引擎");
        return true;
    }

    private void MpvEngine_FrameAvailable()
    {
        // 渲染线程按帧触发：合并通知，避免同一帧排队多次。
        if (Interlocked.Exchange(ref mpvFramePending, 1) == 1) return;
        MpvFrameAvailable?.Invoke();
    }

    /// <summary>引擎真正装载好媒体后刷新"是否有媒体"的状态，并确保引导卡片不再遮挡画面。</summary>
    private void MpvEngine_FileLoaded() => dispatcherQueue.TryEnqueue(() =>
    {
        if (mpvEngine is null) return;
        OnPropertyChanged(nameof(IsMediaOpen));
        // 引擎接管字幕：本项目字幕层随之隐藏。
        OnPropertyChanged(nameof(SubtitleVisibility));
        EmptyStateVisibility = Visibility.Collapsed;
        ApplyEngineSubtitlePreference();
    });

    /// <summary>
    /// 应用记住的字幕选择（关闭字幕 / 上次选中的轨道）。
    /// 没有记录时保持引擎的自动选择，这样切换媒体不会把用户的选择丢掉。
    /// </summary>
    private void ApplyEngineSubtitlePreference()
    {
        if (mpvEngine is null) return;
        try
        {
            if (Settings.EngineSubtitlesOff)
            {
                mpvEngine.SetSubtitleTrack(0);
                return;
            }

            string preference = Settings.EngineSubtitlePreference ?? string.Empty;
            int ordinal = Settings.EngineSubtitleOrdinal;
            if (string.IsNullOrWhiteSpace(preference) && ordinal <= 0) return;

            List<MpvTrackInfo> subtitles = mpvEngine.GetTracks()
                .Where(track => string.Equals(track.Kind, "sub", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (subtitles.Count == 0) return;

            // 先按标题/语言精确匹配（换集后仍然有效），匹配不到再退回到上次的序号。
            int match = -1;
            if (!string.IsNullOrWhiteSpace(preference))
            {
                match = subtitles.FindIndex(track =>
                    string.Equals(track.Title, preference, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(track.Language, preference, StringComparison.OrdinalIgnoreCase));
            }
            if (match < 0 && ordinal >= 1 && ordinal <= subtitles.Count) match = ordinal - 1;
            if (match < 0) return;

            mpvEngine.SetSubtitleTrack(subtitles[match].Id);
        }
        catch (Exception ex)
        {
            AppLogService.Warning("EngineSubtitlePreferenceFailed", "应用记住的字幕选择失败", null, ex);
        }
    }

    private void MpvEngine_EndReached() => dispatcherQueue.TryEnqueue(() =>
    {
        if (mpvEngine is null || currentItem is null) return;
        PlaylistItem finished = currentItem;
        IsPlaying = false;
        playbackHistory.Clear(finished.Path);
        finished.ApplyPlaybackHistory(null);
        _ = playbackHistory.FlushAsync();
        // 单曲循环必须在这里实现：libmpv 引擎播完不会自己重播，而系统播放器的
        // IsLoopingEnabled 在引擎模式下没有媒体源可作用。上面已清掉该文件的续播
        // 记录，因此重播会从头开始。
        if (IsLoopingEnabled)
        {
            PlayItem(finished);
            return;
        }
        if (Settings.AutoPlay) PlayNextAutomatically();
    });

    /// <summary>停止并释放 libmpv 引擎。</summary>
    public void StopMpvPlayback()
    {
        if (mpvEngine is null) return;
        mpvEngine.FrameAvailable -= MpvEngine_FrameAvailable;
        mpvEngine.EndReached -= MpvEngine_EndReached;
        mpvEngine.FileLoaded -= MpvEngine_FileLoaded;
        mpvEngine.Dispose();
        mpvEngine = null;
        IsPlaying = false;
        Interlocked.Exchange(ref mpvFramePending, 0);
        mpvSubtitleTracks.Clear();
        // 引擎停止后"是否有媒体"与字幕归属都会变化，需要按当前状态重算。
        OnPropertyChanged(nameof(IsMediaOpen));
        OnPropertyChanged(nameof(SubtitleVisibility));
        EmptyStateVisibility = IsMediaOpen ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>把播放切到 libmpv 引擎，并从当前位置继续（用于系统解码无法正确呈现的内容）。</summary>
    public bool SwitchCurrentMediaToMpvEngine()
    {
        if (currentItem is null)
        {
            Notify("当前没有打开的媒体文件", true);
            return false;
        }
        double position = IsMpvEngineActive ? 0 : MediaPlayer.PlaybackSession.Position.TotalSeconds;
        MediaPlayer.Pause();
        MediaPlayer.Source = null;
        playbackItem = null;
        return StartMpvPlayback(currentItem, position);
    }

    public ICommand OpenFileCommand { get; }
    public ICommand PlayPauseCommand { get; }
    public ICommand BackCommand { get; }
    public ICommand ForwardCommand { get; }
    public ICommand PreviousCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand StopPlaybackCommand { get; }
    public ICommand ToggleFoldersCommand { get; }
    public ICommand TogglePlaylistCommand { get; }
    public ICommand FullScreenCommand { get; }
    public ICommand SettingsCommand { get; }
    public ICommand ToggleMuteCommand { get; }
    public ICommand SetIntroMarkerCommand { get; }
    public ICommand SetOutroMarkerCommand { get; }
    public string IntroMarkerText => Settings.SkipIntroSeconds > 0
        ? $"清除片头 {FormatTime(TimeSpan.FromSeconds(Settings.SkipIntroSeconds))}" : "设置片头";
    public string OutroMarkerText
    {
        get
        {
            if (Settings.SkipOutroSeconds <= 0) return "设置片尾";
            double start = Math.Max(0, Duration - Settings.SkipOutroSeconds);
            return $"清除片尾 {FormatTime(TimeSpan.FromSeconds(start))}";
        }
    }
    public string Title { get => title; private set => SetProperty(ref title, value); }
    public string CurrentTime { get => currentTime; private set => SetProperty(ref currentTime, value); }
    public string DurationText { get => durationText; private set => SetProperty(ref durationText, value); }
    public string PlayPauseGlyph { get => playPauseGlyph; private set => SetProperty(ref playPauseGlyph, value); }
    public double Duration { get => duration; private set => SetProperty(ref duration, value); }
    public double MediaWidth { get => mediaWidth; private set => SetProperty(ref mediaWidth, value); }
    public double MediaHeight { get => mediaHeight; private set => SetProperty(ref mediaHeight, value); }
    public double ChromeOpacity { get => chromeOpacity; private set => SetProperty(ref chromeOpacity, value); }

    /// <summary>
    /// 当前是否正在播放，由实际生效的播放引擎决定。界面用它判断"全屏空闲时是否隐藏
    /// 指针与控制层"：libmpv 引擎播放时系统会话没有媒体源，若直接读 PlaybackState
    /// 会一直得到"未播放"，导致引擎模式下全屏自动隐藏指针等功能整体失效。
    /// </summary>
    public bool IsPlaying
    {
        get => isPlaying;
        private set => SetProperty(ref isPlaying, value);
    }

    /// <summary>
    /// 单曲循环。系统媒体框架使用自己的 IsLoopingEnabled，libmpv 引擎则在
    /// <see cref="MpvEngine_EndReached"/> 中读取本属性重播，因此统一由这里保存。
    /// </summary>
    public bool IsLoopingEnabled
    {
        get => isLoopingEnabled;
        set
        {
            if (!SetProperty(ref isLoopingEnabled, value)) return;
            MediaPlayer.IsLoopingEnabled = value;
        }
    }
    public Visibility EmptyStateVisibility { get => emptyStateVisibility; private set => SetProperty(ref emptyStateVisibility, value); }
    public Visibility PausedOverlayVisibility { get => pausedOverlayVisibility; private set => SetProperty(ref pausedOverlayVisibility, value); }
    /// <summary>
    /// 是否存在已打开/正在播放的媒体。libmpv 引擎模式下系统会话没有媒体源，
    /// 因此必须把引擎状态一并算进来，否则"清空播放列表""拖入文件"等重算路径
    /// 会把引导卡片重新显示在正在播放的画面上。
    /// </summary>
    public bool IsMediaOpen => currentItem is not null &&
        (playbackItem is not null || (mpvEngine is not null && mpvEngine.HasMedia));
    public bool IsLoading { get => isLoading; private set => SetProperty(ref isLoading, value); }
    public Visibility LoadingVisibility => IsLoading ? Visibility.Visible : Visibility.Collapsed;
    public string LoadingMessage { get => loadingMessage; private set => SetProperty(ref loadingMessage, value); }
    public ObservableRangeCollection<PlaylistItem> Playlist { get; } = new();
    public ObservableRangeCollection<string> Folders { get; } = new();
    public string SubtitleText
    {
        get => subtitleText;
        private set
        {
            if (!SetProperty(ref subtitleText, value)) return;
            OnPropertyChanged(nameof(SubtitleVisibility));
        }
    }
    /// <summary>
    /// 本项目字幕层的可见性。以下任一情况下都不显示本项目的字幕：
    /// 用户手动关闭；libmpv 引擎正在播放（字幕由引擎渲染，避免与片源字幕叠成两行）；
    /// 或当前没有字幕文本。
    /// </summary>
    public Visibility SubtitleVisibility =>
        Settings.HideOwnSubtitles || IsMpvEngineActive || string.IsNullOrWhiteSpace(SubtitleText)
            ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>“本项目字幕”开关变化后刷新字幕层状态。</summary>
    public void ApplyOwnSubtitleSetting()
    {
        OnPropertyChanged(nameof(SubtitleVisibility));
        if (!Settings.HideOwnSubtitles) return;
        // 关闭时立即撤掉已解码的图片字幕，避免残留一帧。
        CancelPgsDecode();
        activePgsSubtitleDocument = null;
        activePgsFrameIndex = -2;
        PgsSubtitleFrameChanged?.Invoke(null);
    }
    // 侧栏可见性触发信号。加载/恢复流程不再主动赋值，侧栏统一由悬停、顶栏按钮或调整块展开，
    // 避免启动或打开文件时列表自动弹出且停留。
    public double LeftSidebarWidth { get => leftSidebarWidth; private set => SetProperty(ref leftSidebarWidth, value); }
    public double RightSidebarWidth { get => rightSidebarWidth; private set => SetProperty(ref rightSidebarWidth, value); }
    public PlaylistItem? SelectedItem
    {
        get => selectedItem;
        set
        {
            if (SetProperty(ref selectedItem, value) && value is not null) Play(value);
        }
    }

    public double Position
    {
        get => position;
        set
        {
            if (!SetProperty(ref position, value)) return;
            if (isSeeking) CurrentTime = FormatTime(TimeSpan.FromSeconds(value));
        }
    }

    public double Volume
    {
        get => volume;
        set
        {
            value = Math.Clamp(value, 0, 100);
            if (!SetProperty(ref volume, value)) return;
            if (IsMpvEngineActive && mpvEngine is not null) mpvEngine.SetVolume(value / 100d);
            else if (!isVolumeFading) MediaPlayer.Volume = value / 100d;
            Settings.Volume = value;
            if (value > 0) volumeBeforeMute = value;
            OnPropertyChanged(nameof(VolumeGlyph));
        }
    }

    public void AdjustVolume(double change)
    {
        Volume += change;
        ShowChrome();
        Notify(Volume <= 0 ? "已静音" : $"音量 {Volume:0}%");
    }

    public void ShowVolumeNotification(double value) =>
        Notify(value <= 0 ? "已静音" : $"音量 {value:0}%");

    public void ShowNotification(string message, bool isError = false) =>
        Notify(message, isError);

    public string VolumeGlyph => Volume <= 0 ? "\uE74F" : Volume < 35 ? "\uE993" : Volume < 70 ? "\uE994" : "\uE767";

    private void ToggleMute()
    {
        Volume = Volume > 0 ? 0 : Math.Max(1, volumeBeforeMute);
        ShowChrome();
        Notify(Volume <= 0 ? "已静音" : $"音量 {Volume:0}%");
    }

    public void BeginSeek() => isSeeking = true;
    public void ChangefromSiseze(Window sender, WindowPosition position)
    {
        if (sender == null) return;

        // MediaWidth/MediaHeight 只由系统 MediaPlayer 的 MediaOpened 事件赋值。
        // 启用 libmpv 杜比引擎后所有媒体都由引擎播放，系统媒体框架从未打开过媒体，
        // 这两个值会一直是 0（或停留在上一个由系统播放的媒体的旧值），
        // 结果是本快捷键在引擎模式下静默失效、或按错误的宽高比计算窗口尺寸。
        // 因此引擎播放时以引擎报告的原始视频尺寸为准。
        double mediaWidth = MediaWidth;
        double mediaHeight = MediaHeight;
        if (IsMpvEngineActive && mpvEngine is not null &&
            mpvEngine.VideoWidth > 0 && mpvEngine.VideoHeight > 0)
        {
            mediaWidth = mpvEngine.VideoWidth;
            mediaHeight = mpvEngine.VideoHeight;
        }
        if (mediaWidth <= 0 || mediaHeight <= 0) return; // 防止除零或视频未加载

        // 1. 获取当前窗口所在显示器的尺寸
        var hWnd = WindowNative.GetWindowHandle(sender);
        var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
        var displayArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
        var workArea = displayArea.WorkArea;
        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);

        // 2. 计算窗口宽度 = 屏幕宽度的一半
        int windowWidth = workArea.Width / 2;

        // 3. 根据视频宽高比等比例计算窗口高度
        double aspectRatio = mediaHeight / mediaWidth;
        int windowHeight = (int)(windowWidth * aspectRatio);

        // 4. 设置窗口大小
        appWindow.Resize(new SizeInt32(windowWidth, windowHeight));

        // 5. 计算目标位置并移动窗口
        var (posX, posY) = CalculatePosition(position, workArea, windowWidth, windowHeight);
        appWindow.Move(new PointInt32(posX, posY));
    }
    private (int X, int Y) CalculatePosition(WindowPosition position, RectInt32 workArea, int windowWidth, int windowHeight)
    {
        switch (position)
        {
            case WindowPosition.TopLeft:
                return (workArea.X, workArea.Y);

            case WindowPosition.BottomLeft:
                return (workArea.X, workArea.Y + workArea.Height - windowHeight);

            case WindowPosition.TopRight:
                return (workArea.X + workArea.Width - windowWidth, workArea.Y);

            case WindowPosition.BottomRight:
                return (workArea.X + workArea.Width - windowWidth, workArea.Y + workArea.Height - windowHeight);

            case WindowPosition.Center:
                return (workArea.X + (workArea.Width - windowWidth) / 2,
                        workArea.Y + (workArea.Height - windowHeight) / 2);
            default:
                return (workArea.X, workArea.Y);
        }
    }
    public async Task RestoreLastPlaybackAsync()
    {
        using IDisposable loading = BeginLoading("正在恢复播放列表…");
        // 单独恢复文件夹根节点，避免根据媒体路径反推目录而破坏用户保存的文件夹树。
        if (Playlist.Count > 0 || currentItem is not null) return;

        await playbackHistory.CleanupAsync(
            Settings.HistoryRetentionDays, Settings.MaxPlaybackHistoryEntries);
        await playbackHistory.FlushAsync();

        await RestorePersistedListsAsync();

        // 启动时不主动展开播放列表侧栏，保持悬浮面板“悬停/手动展开”的既定行为；
        // 否则侧栏会在无鼠标交互时一直停留（隐藏期限只在指针移动后设置）。
        // 侧栏宽度仍由 ApplySettings 从设置读取，用户展开时尺寸不变。

        if (!Settings.AutoPlay) return;
        string? lastPath = await playbackHistory.GetMostRecentPlayablePathAsync();
        if (string.IsNullOrWhiteSpace(lastPath)) return;

        PlaylistItem? lastItem = Playlist.FirstOrDefault(item =>
            string.Equals(item.Path, lastPath, StringComparison.OrdinalIgnoreCase));
        if (lastItem is null)
        {
            try
            {
                StorageFile file = await StorageFile.GetFileFromPathAsync(lastPath);
                if (!IsSupportedMedia(file)) return;
                lastItem = CreatePlaylistItem(file);
                Playlist.Add(lastItem);
            }
            catch { return; }
        }
        SelectedItem = lastItem;
    }

    /// <summary>
    /// 恢复上次保存的媒体文件夹列表与播放列表。
    /// 必须由“带参数的启动”也调用：播放列表只在内存里维护，退出时会把内存内容写回
    /// playlist.json，若不先恢复，一次带参数的启动就会把用户保存的列表写成空列表。
    /// </summary>
    private async Task RestorePersistedListsAsync()
    {
        if (Playlist.Count > 0 || currentItem is not null) return;

        // 单独恢复文件夹根节点，避免根据媒体路径反推目录而破坏用户保存的文件夹树。
        PlaylistPersistenceService.PersistedState persistedState = playlistPersistence.Load();
        Folders.ReplaceAll(persistedState.FolderPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase));

        var restoredItems = new List<PlaylistItem>();
        IReadOnlyList<StorageFile> restoredFiles = await LoadStorageFilesAsync(
            persistedState.MediaPaths);
        foreach (StorageFile file in restoredFiles)
        {
            if (!IsSupportedMedia(file) || restoredItems.Any(item =>
                string.Equals(item.Path, file.Path, StringComparison.OrdinalIgnoreCase))) continue;
            restoredItems.Add(CreatePlaylistItem(file));
        }
        Playlist.ReplaceAll(restoredItems);
        StartPlaylistHistoryResolution(Playlist);
        EmptyStateVisibility = IsMediaOpen ? Visibility.Collapsed : Visibility.Visible;
    }

    public async Task LoadStartupArgumentsAsync(IReadOnlyList<string> arguments)
    {
        var storageItems = new List<IStorageItem>();
        string? requestedPlayPath = null;
        // 模式属于整个进程（由启动参数决定，见 AppMode）：隐私模式下本次启动只播放指定文件，
        // 不把它加入播放列表/媒体文件夹列表，记录也只写隐私记录文件。
        bool privacyLaunch = AppMode.IsPrivacy;

        foreach (string rawArgument in arguments)
        {
            if (string.IsNullOrWhiteSpace(rawArgument)) continue;
            string argument = rawArgument.Trim().Trim('"');

            if (argument.StartsWith("Records:", StringComparison.OrdinalIgnoreCase))
                continue;

            // 开关形式 --privacy（兼容 -privacy、/privacy）已由 AppMode 消费，这里跳过。
            if (argument.Equals("--privacy", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("-privacy", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("/privacy", StringComparison.OrdinalIgnoreCase))
                continue;

            // 前缀形式 Privacy:<路径> 等价于 Play:（隐私属性由 AppMode 决定）。
            bool isPrivacyArgument = argument.StartsWith("Privacy:", StringComparison.OrdinalIgnoreCase);
            bool isPlayArgument = argument.StartsWith("Play:", StringComparison.OrdinalIgnoreCase);
            bool isFolderArgument = argument.StartsWith("Folder:", StringComparison.OrdinalIgnoreCase);
            string path = isPrivacyArgument ? argument[8..]
                : isPlayArgument ? argument[5..]
                : isFolderArgument ? argument[7..]
                : argument;
            path = path.Trim().Trim('"');

            try
            {
                if (System.IO.File.Exists(path))
                {
                    StorageFile file = await StorageFile.GetFileFromPathAsync(path);
                    storageItems.Add(file);
                    if (isPrivacyArgument || isPlayArgument || requestedPlayPath is null)
                        requestedPlayPath = file.Path;
                }
                else if (System.IO.Directory.Exists(path))
                {
                    storageItems.Add(await StorageFolder.GetFolderFromPathAsync(path));
                }
            }
            catch (Exception ex)
            {
                // 单个参数失败（路径不存在、无权限、文件被占用等）不应中断其余参数的加载，
                // 但也不能静默丢弃：否则用户传了错误路径却得不到任何反馈。
                // 隐私模式下不记录路径本身。
                AppLogService.Warning("StartupArgumentSkipped", "启动参数无法作为媒体或文件夹加载",
                    new { Path = privacyLaunch ? null : path }, ex);
            }
        }

        // 隐私模式不记录媒体路径，避免日志泄漏隐私播放内容；只记录数量。
        AppLogService.Information("StartupArgumentsParsed", "启动参数解析完成", new
        {
            PrivacyMode = privacyLaunch,
            StorageItems = storageItems.Count,
            HasRequestedPlayPath = !string.IsNullOrWhiteSpace(requestedPlayPath),
            RequestedPlayPath = privacyLaunch ? null : requestedPlayPath
        });

        if (storageItems.Count == 0) return;

        if (privacyLaunch)
        {
            // 先恢复上次保存的列表（否则退出时会把空列表写回，等于清掉用户保存的列表），
            // 再只播放明确指定的那个文件、不把它加入列表：
            // 于是播放列表与媒体文件夹列表的内容、顺序都与上次完全一致。
            await RestorePersistedListsAsync();
            if (!string.IsNullOrWhiteSpace(requestedPlayPath))
                await PlayDetachedAsync(requestedPlayPath);
            return;
        }

        await AddDroppedItemsAsync(storageItems);

        if (string.IsNullOrWhiteSpace(requestedPlayPath)) return;
        PlaylistItem? requestedItem = Playlist.FirstOrDefault(item =>
            string.Equals(item.Path, requestedPlayPath, StringComparison.OrdinalIgnoreCase));
        if (requestedItem is null) return;

        forcePlayOnMediaOpened = true;
        SelectedItem = requestedItem;
    }

    public async Task AddDroppedItemsAsync(IReadOnlyList<IStorageItem> items)
    {
        using IDisposable loading = BeginLoading(
            items.Any(item => item is StorageFolder) ? "正在扫描文件夹…" : "正在加载媒体文件…");
        // 拖入的文件插到列表顶部并立即播放；明确拖入文件夹时则替换文件夹根节点和播放列表，
        // 从而保持两种拖放操作的既定行为。
        var files = new List<StorageFile>();
        var droppedFolders = new List<string>();
        var scannedPaths = new List<string>();

        foreach (IStorageItem item in items)
        {
            if (item is StorageFile file && IsSupportedMedia(file))
                files.Add(file);
            else if (item is StorageFolder folder)
            {
                droppedFolders.Add(folder.Path);
                MediaFolderScanResult scan = await ScanMediaFilesAsync(folder.Path);
                scannedPaths.AddRange(scan.Paths);
                if (scan.Error is not null)
                    AppLogService.Warning("MediaFolderScanFailed", "扫描拖入的媒体文件夹失败",
                        new { folder.Path }, scan.Error);
            }
        }

        files.AddRange(await LoadStorageFilesAsync(scannedPaths));

        if (files.Count == 0 && droppedFolders.Count == 0)
        {
            Notify("没有可加载的媒体文件", true);
            return;
        }

        if (droppedFolders.Count == 0)
        {
            var insertedItems = new List<PlaylistItem>();
            var remainingItems = Playlist.ToList();
            foreach (StorageFile file in files
                .DistinctBy(file => file.Path, StringComparer.OrdinalIgnoreCase))
            {
                PlaylistItem? item = remainingItems.FirstOrDefault(existing =>
                    string.Equals(existing.Path, file.Path, StringComparison.OrdinalIgnoreCase));
                if (item is not null)
                    remainingItems.Remove(item);
                else
                    item = CreatePlaylistItem(file);

                insertedItems.Add(item);
            }
            Playlist.ReplaceAll(insertedItems.Concat(remainingItems));
            StartPlaylistHistoryResolution(Playlist);
            PlaylistItem? firstDroppedItem = insertedItems.FirstOrDefault();

            EmptyStateVisibility = IsMediaOpen ? Visibility.Collapsed : Visibility.Visible;
            if (firstDroppedItem is not null)
            {
                forcePlayOnMediaOpened = true;
                SelectedItem = firstDroppedItem;
            }
            ShowChrome();
            Notify($"已添加并播放 {insertedItems.Count} 个媒体文件");
            return;
        }

        SelectedItem = null;

        // 拖入文件夹时同时替换文件夹树和播放列表。
        if (droppedFolders.Count > 0)
            Folders.ReplaceAll(droppedFolders
                .Where(folder => !string.IsNullOrWhiteSpace(folder))
                .Distinct(StringComparer.OrdinalIgnoreCase));

        Playlist.ReplaceAll(files
            .DistinctBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .OrderBy(file => file.Path, StringComparer.CurrentCultureIgnoreCase)
            .Select(CreatePlaylistItem));
        StartPlaylistHistoryResolution(Playlist);

        EmptyStateVisibility = IsMediaOpen ? Visibility.Collapsed : Visibility.Visible;
        ShowChrome();
        Notify($"已加载 {droppedFolders.Count} 个文件夹，共 {Playlist.Count} 个媒体文件");
    }

    public async Task LoadFolderAsync(string folderPath)
    {
        using IDisposable loading = BeginLoading("正在扫描文件夹中的媒体…");
        // 双击 TreeView 节点时只替换播放列表，保留文件夹树及其展开状态。
        if (string.IsNullOrWhiteSpace(folderPath)) return;
        MediaFolderScanResult scan = await ScanMediaFilesAsync(folderPath);
        if (scan.Error is not null)
        {
            AppLogService.Error("MediaFolderLoadFailed", "加载媒体文件夹失败",
                new { FolderPath = folderPath }, scan.Error);
            Notify("文件夹不存在或当前无法访问", true);
            return;
        }
        IReadOnlyList<string> paths = scan.Paths;
        List<StorageFile> files = (await LoadStorageFilesAsync(paths)).ToList();

        SelectedItem = null;
        Playlist.ReplaceAll(files
            .DistinctBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .OrderBy(file => file.Path, StringComparer.CurrentCultureIgnoreCase)
            .Select(CreatePlaylistItem));
        StartPlaylistHistoryResolution(Playlist);

        // 此处有意不修改 Folders，以保留当前文件夹树和展开状态。
        // 用户已经主动选择了目录，即使目录中没有媒体，也不再显示首次启动引导卡片；
        // 空目录结果由顶部 InfoBar 提示即可。播放列表侧栏不主动展开，
        // 统一通过悬停、顶栏按钮或调整块展开。
        ShowChrome();
        Notify(Playlist.Count == 0
            ? "已加载文件夹，当前目录没有支持的媒体文件"
            : $"已加载 {Playlist.Count} 个媒体文件");
    }

    public Task<IReadOnlyList<FolderTreeItem>> GetFolderTreeItemsAsync(
        IReadOnlyList<string> folderPaths) =>
        Task.Run<IReadOnlyList<FolderTreeItem>>(() => folderPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(CreateFolderTreeItem)
            .ToArray());

    public Task<IReadOnlyList<FolderTreeItem>> GetSubfoldersAsync(string folderPath) =>
        Task.Run<IReadOnlyList<FolderTreeItem>>(() =>
        {
            try
            {
                return Directory.EnumerateDirectories(folderPath)
                    .OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase)
                    // 只把当前层目录加入 TreeView；对子目录仅探测是否至少还有一层，
                    // 用于决定是否显示展开标识，不在此处构建孙级节点。
                    .Select(CreateFolderTreeItem)
                    .ToArray();
            }
            catch (UnauthorizedAccessException ex)
            {
                AppLogService.Warning("SubfolderEnumerationDenied", "没有权限读取子文件夹",
                    new { FolderPath = folderPath }, ex);
                return Array.Empty<FolderTreeItem>();
            }
            catch (IOException ex)
            {
                AppLogService.Warning("SubfolderEnumerationFailed", "读取子文件夹失败",
                    new { FolderPath = folderPath }, ex);
                return Array.Empty<FolderTreeItem>();
            }
        });

    private static FolderTreeItem CreateFolderTreeItem(string path)
    {
        bool hasSubfolders;
        try
        {
            hasSubfolders = Directory.EnumerateDirectories(path).Take(1).Any();
        }
        catch (UnauthorizedAccessException)
        {
            hasSubfolders = false;
        }
        catch (IOException)
        {
            hasSubfolders = false;
        }
        return new FolderTreeItem(path, hasSubfolders);
    }

    private static bool IsSupportedMedia(StorageFile file) =>
        SupportedMediaExtensions.Contains(file.FileType);

    private static Task<MediaFolderScanResult> ScanMediaFilesAsync(string rootPath) =>
        Task.Run(() =>
        {
            // 只加载当前文件夹的直属媒体文件。子文件夹由左侧 TreeView 展开后
            // 单独选择加载，避免一次选择根目录时把整个目录树加入播放列表。
            try
            {
                IReadOnlyList<string> paths = Directory.EnumerateFiles(rootPath)
                    .Where(file => SupportedMediaExtensions.Contains(Path.GetExtension(file)))
                    .OrderBy(file => file, StringComparer.CurrentCultureIgnoreCase)
                    .ToArray();
                return new MediaFolderScanResult(paths, null);
            }
            catch (UnauthorizedAccessException ex)
            {
                return new MediaFolderScanResult(Array.Empty<string>(), ex);
            }
            catch (IOException ex)
            {
                return new MediaFolderScanResult(Array.Empty<string>(), ex);
            }
        });

    private static async Task<IReadOnlyList<StorageFile>> LoadStorageFilesAsync(
        IEnumerable<string> mediaPaths)
    {
        // 网络目录逐文件串行创建 StorageFile 会让加载时间线性增长；限制为 4 路并发，
        // 在加快 SMB 访问的同时避免一次性压满网络或文件服务器。
        string[] paths = mediaPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        using var gate = new SemaphoreSlim(4, 4);
        Task<StorageFile?>[] tasks = paths.Select(async path =>
        {
            await gate.WaitAsync();
            try
            {
                return await StorageFile.GetFileFromPathAsync(path);
            }
            catch (Exception ex)
            {
                AppLogService.Warning("MediaFileOpenFailed", "创建媒体文件对象失败",
                    new { MediaPath = path }, ex);
                return null;
            }
            finally
            {
                gate.Release();
            }
        }).ToArray();

        StorageFile?[] results = await Task.WhenAll(tasks);
        return results.Where(file => file is not null).Cast<StorageFile>().ToArray();
    }

    private sealed record MediaFolderScanResult(IReadOnlyList<string> Paths, Exception? Error);

    public void SeekRelative(double seconds) => SeekBy(TimeSpan.FromSeconds(seconds));
    public void StopPlayback()
    {
        bool mpvActive = IsMpvEngineActive;
        if (currentItem is null || (!mpvActive && MediaPlayer.Source is null))
        {
            Notify("当前没有打开的媒体文件");
            return;
        }

        // “停止”代表关闭当前媒体，而不是把播放位置重置为零后暂停。
        // 先保存有效进度，再使旧媒体源、轨道和音量任务全部失效。
        SaveCurrentPosition(true);
        Interlocked.Increment(ref mediaGeneration);
        CancelVolumeFade(false);
        pendingVolumeFadeIn = false;
        pendingResumePosition = 0;
        forcePlayOnMediaOpened = false;
        isSkippingOutro = false;
        DetachSubtitleTrack();
        ClearExternalSubtitleState();
        if (mpvActive) StopMpvPlayback();

        MediaPlayer.Pause();
        MediaPlayer.Source = null;
        playbackItem = null;
        currentItem = null;
        SelectedItem = null;

        Position = 0;
        Duration = 1;
        CurrentTime = "00:00";
        DurationText = "00:00";
        Title = "本地资源播放器";
        PlayPauseGlyph = "\uE768";
        PausedOverlayVisibility = Visibility.Collapsed;
        IsPlaying = false;
        EmptyStateVisibility = Visibility.Visible;
        SubtitleText = string.Empty;
        OnPropertyChanged(nameof(OutroMarkerText));
        OnPropertyChanged(nameof(IsMediaOpen));
        ShowChrome();
        Notify("已停止并关闭当前媒体文件");
    }
    public void RestartPlayback()
    {
        MediaPlayer.PlaybackSession.Position = TimeSpan.Zero;
        MediaPlayer.Play();
        ShowChrome();
        Notify("已重新开始播放");
    }

    /// <summary>
    /// 切换“自绘 / 直通”呈现模式后重新装载当前媒体，使新呈现模式立即生效。
    /// 帧服务器开关只在媒体管线重新建立时才真正改变输出路径，因此这里清空再赋回同一播放项，
    /// 并沿用原播放位置与播放状态。
    /// </summary>
    public void ReloadCurrentMediaForRenderMode()
    {
        ApplyRenderModeSetting();
        string modeName = Settings.HdrDirectPresent ? "系统直通呈现" : "帧服务器自绘";

        if (currentItem is null || playbackItem is null || MediaPlayer.Source is null)
        {
            Notify($"已切换为{modeName}");
            return;
        }

        try
        {
            MediaPlaybackSession session = MediaPlayer.PlaybackSession;
            pendingResumePosition = session.Position.TotalSeconds;
            forcePlayOnMediaOpened = session.PlaybackState == MediaPlaybackState.Playing;
            MediaPlayer.Source = null;
            MediaPlayer.Source = playbackItem;
            Notify($"已切换为{modeName}，正在重新打开当前媒体");
        }
        catch (Exception ex)
        {
            AppLogService.Warning("RenderModeReloadFailed", "重新装载媒体以切换呈现模式失败",
                new { DirectPresent = Settings.HdrDirectPresent }, ex);
            Notify("切换呈现模式失败，请手动重新打开该媒体", true);
        }
    }

    public void PlayItem(PlaylistItem item)
    {
        forcePlayOnMediaOpened = true;
        if (ReferenceEquals(SelectedItem, item)) Play(item);
        else SelectedItem = item;
    }

    /// <summary>从头播放：清除本模式里该文件的续播位置后重新开始。</summary>
    public async Task PlayFromBeginningAsync(PlaylistItem item)
    {
        playbackHistory.Clear(item.Path);
        item.ApplyPlaybackHistory(null);
        await playbackHistory.FlushAsync();
        // 防止 Play() 在替换同一个媒体源前把旧播放位置重新写回。
        historySuppressedForPath = item.Path;
        ignoreResumeOnce = true;
        PlayItem(item);
        Notify("已从头播放并清除该文件的续播记录");
    }

    /// <summary>
    /// 用户主动清除该文件的播放记录。只影响当前模式的记录文件：
    /// 在隐私窗口里清除的是隐私记录，普通模式的记录不受影响（反之亦然）。
    /// </summary>
    public async Task ClearPlaybackHistoryAsync(PlaylistItem item)
    {
        if (!playbackHistory.Clear(item.Path))
        {
            Notify("该文件没有播放记录");
            return;
        }

        item.ApplyPlaybackHistory(null);
        if (ReferenceEquals(currentItem, item)) historySuppressedForPath = item.Path;
        await playbackHistory.FlushAsync();
        Notify("已清除该文件的播放记录");
    }

    /// <summary>清空<b>当前模式</b>的全部播放记录（另一种模式的记录文件不受影响）。</summary>
    public async Task ClearAllPlaybackHistoryAsync()
    {
        int count = playbackHistory.ClearAll();
        foreach (PlaylistItem item in Playlist) item.ApplyPlaybackHistory(null);
        historySuppressedForPath = currentItem?.Path;
        await playbackHistory.FlushAsync();
        string mode = AppMode.IsPrivacy ? "隐私模式" : "普通模式";
        Notify(count > 0 ? $"已清空{mode}的 {count} 条播放记录" : $"{mode}的播放记录已经为空");
    }

    /// <summary>
    /// 清空两种模式的全部播放记录。这是唯一允许的跨模式写入，且只由用户主动触发：
    /// 当前模式走本实例，另一种模式临时构造一个服务实例只做清空与落盘。
    /// </summary>
    public async Task ClearBothModesHistoryAsync()
    {
        int count = playbackHistory.ClearAll();
        foreach (PlaylistItem item in Playlist) item.ApplyPlaybackHistory(null);
        historySuppressedForPath = currentItem?.Path;
        await playbackHistory.FlushAsync();

        int otherCount = 0;
        using (var other = new PlaybackHistoryService(!AppMode.IsPrivacy))
        {
            otherCount = other.ClearAll();
            await other.FlushAsync();
        }

        int total = count + otherCount;
        Notify(total > 0 ? $"已清空两种模式的记录，共 {total} 条" : "两种模式的播放记录都已经为空");
    }

    public async Task ApplyPlaybackHistorySettingsAsync()
    {
        await playbackHistory.CleanupAsync(
            Settings.HistoryRetentionDays, Settings.MaxPlaybackHistoryEntries);
        await playbackHistory.FlushAsync();
        RefreshPlaylistHistory();
    }
    public void SetPlaybackRate(double rate)
    {
        if (IsMpvEngineActive && mpvEngine is not null) mpvEngine.SetSpeed(rate);
        else MediaPlayer.PlaybackSession.PlaybackRate = rate;
        Notify($"播放速度 {rate:0.##}x");
    }
    public void RefreshSkipSettings()
    {
        OnPropertyChanged(nameof(IntroMarkerText));
        OnPropertyChanged(nameof(OutroMarkerText));
    }
    private void ToggleIntroMarker()
    {
        bool clearing = Settings.SkipIntroSeconds > 0;
        Settings.SkipIntroSeconds = clearing ? 0 : Math.Max(0, Position);
        persistSettings();
        OnPropertyChanged(nameof(IntroMarkerText));
        Notify(clearing ? "已清除片头跳过位置" : $"片头结束位置：{FormatTime(TimeSpan.FromSeconds(Settings.SkipIntroSeconds))}");
    }

    private void ToggleOutroMarker()
    {
        bool clearing = Settings.SkipOutroSeconds > 0;
        Settings.SkipOutroSeconds = clearing
            ? 0 : Math.Max(0, Duration - Position);
        persistSettings();
        OnPropertyChanged(nameof(OutroMarkerText));
        Notify(clearing ? "已清除片尾跳过位置" : $"片尾提前跳过：{FormatTime(TimeSpan.FromSeconds(Settings.SkipOutroSeconds))}");
    }

    public void SetIntroMarkerPosition(double seconds, bool save)
    {
        double outroStart = Settings.SkipOutroSeconds > 0
            ? Math.Max(0, Duration - Settings.SkipOutroSeconds) : Duration;
        Settings.SkipIntroSeconds = Math.Clamp(seconds, 0, Math.Max(0, outroStart - 1));
        if (save) persistSettings();
        OnPropertyChanged(nameof(IntroMarkerText));
        if (save) Notify($"片头结束位置：{FormatTime(TimeSpan.FromSeconds(Settings.SkipIntroSeconds))}");
    }

    public void SetOutroMarkerPosition(double startSeconds, bool save)
    {
        double start = Math.Clamp(startSeconds, Math.Min(Duration, Settings.SkipIntroSeconds + 1), Duration);
        Settings.SkipOutroSeconds = Math.Max(0, Duration - start);
        if (save) persistSettings();
        OnPropertyChanged(nameof(OutroMarkerText));
        if (save) Notify($"片尾开始位置：{FormatTime(TimeSpan.FromSeconds(start))}");
    }
    public void EndSeek()
    {
        // libmpv 引擎：把拖动结束时的位置交给引擎（系统会话此时没有媒体源）。
        if (IsMpvEngineActive && mpvEngine is not null)
        {
            try
            {
                mpvEngine.Seek(Position);
            }
            catch (Exception ex)
            {
                AppLogService.Warning("MpvSeekFailed", "libmpv 定位失败", new { Position }, ex);
            }
            isSeeking = false;
            ShowChrome();
            return;
        }

        if (MediaPlayer.PlaybackSession.NaturalDuration > TimeSpan.Zero)
            MediaPlayer.PlaybackSession.Position = TimeSpan.FromSeconds(Position);
        isSeeking = false;
        ShowChrome();
    }

    public void ShowChrome()
    {
        ChromeOpacity = 1;
        chromeTimer.Stop();
        chromeTimer.Start();
    }

    private async Task OpenFileAsync()
    {
        IReadOnlyList<StorageFile> files = await pickMediaFiles();
        if (files.Count == 0) return;
        using IDisposable loading = BeginLoading("正在加载媒体文件…");
        SelectedItem = null;
        Playlist.ReplaceAll(files
            .Where(IsSupportedMedia)
            .DistinctBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .Select(CreatePlaylistItem));
        StartPlaylistHistoryResolution(Playlist);
        Folders.ReplaceAll(files
            .Select(file => System.IO.Path.GetDirectoryName(file.Path))
            .Where(folder => !string.IsNullOrWhiteSpace(folder))
            .Select(folder => folder!)
            .Distinct(StringComparer.OrdinalIgnoreCase));
        // 不主动展开播放列表侧栏，保持“悬停/手动展开”的一致行为。
        EmptyStateVisibility = Visibility.Collapsed;
        ShowChrome();
        Notify($"已加载 {Playlist.Count} 个媒体文件");
    }

    /// <summary>显示可叠加的加载状态；最后一个加载操作结束后自动隐藏。</summary>
    public IDisposable BeginLoading(string message)
    {
        LoadingMessage = message;
        loadingOperationCount++;
        if (!IsLoading)
        {
            IsLoading = true;
            OnPropertyChanged(nameof(LoadingVisibility));
        }
        return new LoadingScope(this);
    }

    public void ClearFolders()
    {
        int count = Folders.Count;
        if (count == 0)
        {
            Notify("媒体文件夹列表已经为空");
            return;
        }

        Folders.ReplaceAll(Array.Empty<string>());
        playlistPersistence.Save(Playlist.Select(item => item.Path), Folders);
        Notify(AppMode.IsPrivacy
            ? $"已清空 {count} 个媒体文件夹（隐私模式不写回列表文件）"
            : $"已清空 {count} 个媒体文件夹");
    }

    public void ClearPlaylist()
    {
        int count = Playlist.Count;
        if (count == 0)
        {
            Notify("播放列表已经为空");
            return;
        }

        // 清理列表不打断当前媒体，避免误触按钮造成播放中断。
        SelectedItem = null;
        Playlist.ReplaceAll(Array.Empty<PlaylistItem>());
        StartPlaylistHistoryResolution(Playlist);
        // 引导卡片只在“没有文件播放”时显示；清空列表但仍在播放时不应遮挡画面。
        EmptyStateVisibility = IsMediaOpen ? Visibility.Collapsed : Visibility.Visible;
        playlistPersistence.Save(Playlist.Select(item => item.Path), Folders);
        Notify(AppMode.IsPrivacy
            ? $"已清空播放列表中的 {count} 个项目（隐私模式不写回列表文件）"
            : $"已清空播放列表中的 {count} 个项目");
    }

    private void EndLoading()
    {
        if (loadingOperationCount > 0) loadingOperationCount--;
        if (loadingOperationCount != 0 || !IsLoading) return;
        IsLoading = false;
        OnPropertyChanged(nameof(LoadingVisibility));
    }

    private sealed class LoadingScope(MainViewModel owner) : IDisposable
    {
        private MainViewModel? owner = owner;
        public void Dispose() => Interlocked.Exchange(ref owner, null)?.EndLoading();
    }

    private void Notify(string message, bool isError = false) =>
        NotificationRequested?.Invoke(this, new UserNotificationEventArgs(message, isError));

    /// <summary>
    /// libmpv 引擎的播放入口：解析续播位置后交给引擎。
    /// </summary>
    private async Task PlayWithMpvEngineAsync(PlaylistItem item)
    {
        SaveCurrentPosition(true);
        historySuppressedForPath = null;
        CancelVolumeFade(false);
        long generation = Interlocked.Increment(ref mediaGeneration);

        double startSeconds = 0;
        try
        {
            PlaybackHistoryRecord? record = await Task.Run(() =>
                playbackHistory.ResolveAsync(item.Path, prepareIdentity: true));
            if (generation != mediaGeneration) return;
            item.ApplyPlaybackHistory(record);
            if (Settings.ResumePlaybackPosition && record is PlaybackHistoryRecord history)
                startSeconds = history.Position;
            // 与系统会话路径一致：续播位置之外还要考虑"片头跳过"设置。
            startSeconds = Math.Max(Settings.SkipIntroSeconds, startSeconds);
            _ = playbackHistory.FlushIfDueAsync(TimeSpan.FromSeconds(1));
        }
        catch (Exception ex)
        {
            AppLogService.Warning("PlaybackHistoryResolveFailed", "匹配媒体播放记录失败",
                new { item.Path }, ex);
            if (generation != mediaGeneration) return;
        }

        if (generation != mediaGeneration) return;
        StartMpvPlayback(item, startSeconds);
    }

    private async void Play(PlaylistItem item)
    {
        // 开启 libmpv 引擎时，播放整体交给引擎（系统媒体框架无法正确呈现杜比视界 Profile 5）。
        if (Settings.UseLibMpvEngine && IsMpvEngineAvailable)
        {
            await PlayWithMpvEngineAsync(item);
            return;
        }

        // 在 MediaPlayer 开始解析下一项之前，先清理当前媒体源专属状态。
        SaveCurrentPosition(true);
        historySuppressedForPath = null;
        CancelVolumeFade(false);
        long generation = Interlocked.Increment(ref mediaGeneration);

        // 文件可能已被移动或重命名。实际打开媒体前异步准备轻量指纹，
        // 并尝试把旧路径记录迁移到当前路径；快速连续切换时只保留最后一次请求。
        PlaybackHistoryRecord? resolvedHistory = null;
        try
        {
            // ResolveAsync 在真正异步读取前需要访问 FileInfo/FileStream；放入线程池，
            // 避免网络文件响应缓慢时阻塞 WinUI 消息循环。
            resolvedHistory = await Task.Run(() =>
                playbackHistory.ResolveAsync(item.Path, prepareIdentity: true));
            if (generation != mediaGeneration) return;
            item.ApplyPlaybackHistory(resolvedHistory);
            _ = playbackHistory.FlushIfDueAsync(TimeSpan.FromSeconds(1));
        }
        catch (Exception ex)
        {
            AppLogService.Warning("PlaybackHistoryResolveFailed", "匹配媒体播放记录失败",
                new { item.Path }, ex);
            if (generation != mediaGeneration) return;
        }

        pendingVolumeFadeIn = Settings.EnableVolumeFadeIn && Settings.VolumeFadeInDuration > 0 && Volume > 0;
        MediaPlayer.Volume = pendingVolumeFadeIn ? 0 : Volume / 100d;
        currentItem = item;
        EmptyStateVisibility = Visibility.Collapsed;
        pendingResumePosition = !ignoreResumeOnce && Settings.ResumePlaybackPosition &&
            resolvedHistory is PlaybackHistoryRecord record ? record.Position : 0;
        ignoreResumeOnce = false;
        isSkippingOutro = false;
        Title = item.Name;
        DetachSubtitleTrack();
        ClearExternalSubtitleState();
        activeMediaSource = MediaSource.CreateFromStorageFile(item.File);
        playbackItem = new MediaPlaybackItem(activeMediaSource);
        MediaPlayer.Source = playbackItem;
        OnPropertyChanged(nameof(IsMediaOpen));
        _ = AutoLoadExternalSubtitlesAsync(item, activeMediaSource, mediaGeneration);
    }

    private PlaylistItem CreatePlaylistItem(StorageFile file)
    {
        var item = new PlaylistItem(file);
        ApplyPlaybackHistory(item);
        return item;
    }

    /// <summary>
    /// 隐私打开：只播放指定文件，不把它加入播放列表，也不改动媒体文件夹列表，
    /// 因此两个列表在本次会话与退出写回时都保持原样；播放记录照常保存，之后仍可从上次位置续播。
    /// </summary>
    private async Task PlayDetachedAsync(string path)
    {
        // 隐私模式不把媒体完整路径写进日志。
        AppLogService.Information("PrivacyPlayDetached", "隐私打开：只播放该文件、不加入播放列表",
            AppMode.IsPrivacy
                ? new { FileName = System.IO.Path.GetFileName(path) }
                : new { Path = path });
        StorageFile file;
        try
        {
            file = await StorageFile.GetFileFromPathAsync(path);
        }
        catch (Exception ex)
        {
            AppLogService.Warning("PrivacyPlayOpenFailed", "隐私打开的文件无法访问",
                new { Path = path }, ex);
            Notify($"无法打开文件：{System.IO.Path.GetFileName(path)}", true);
            return;
        }

        if (!IsSupportedMedia(file))
        {
            Notify($"不支持的媒体文件：{file.Name}", true);
            return;
        }

        // 这个文件不属于播放列表：清掉选中项，避免列表出现与内容不一致的选中状态。
        if (selectedItem is not null)
        {
            selectedItem = null;
            OnPropertyChanged(nameof(SelectedItem));
        }
        // 与显式指定文件启动一致：即使关闭了自动播放也要立刻播放。
        forcePlayOnMediaOpened = true;
        Play(CreatePlaylistItem(file));
    }

    private void ApplyPlaybackHistory(PlaylistItem item)
    {
        if (playbackHistory.TryGet(item.Path, out PlaybackHistoryRecord record))
        {
            item.ApplyPlaybackHistory(record);
            return;
        }

        item.ApplyPlaybackHistory(null);
    }

    private void StartPlaylistHistoryResolution(IEnumerable<PlaylistItem> items)
    {
        playlistHistoryResolutionCancellation?.Cancel();
        playlistHistoryResolutionCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        playlistHistoryResolutionCancellation = cancellation;
        PlaylistItem[] snapshot = items.ToArray();
        _ = ResolveMovedPlaybackHistoryBatchAsync(snapshot, cancellation.Token);
    }

    private async Task ResolveMovedPlaybackHistoryBatchAsync(
        IReadOnlyList<PlaylistItem> items, CancellationToken cancellationToken)
    {
        try
        {
            // 顺序、限流地在后台匹配，避免加载含大量媒体的网络目录时同时启动
            // 数百个 FileInfo/哈希读取任务。每项完成后只在 UI 线程更新一次绑定。
            foreach (PlaylistItem item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PlaybackHistoryRecord? record = await Task.Run(
                    () => playbackHistory.ResolveAsync(item.Path), cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (record is not null) item.ApplyPlaybackHistory(record);
            }
            await playbackHistory.FlushIfDueAsync(TimeSpan.FromSeconds(1));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLogService.Warning("PlaybackHistoryListResolveFailed",
                "批量匹配移动后的播放记录失败", new { ItemCount = items.Count }, ex);
        }
    }

    private void RefreshPlaylistHistory()
    {
        foreach (PlaylistItem item in Playlist) ApplyPlaybackHistory(item);
        StartPlaylistHistoryResolution(Playlist);
    }

    private void StartPendingVolumeFade()
    {
        if (!pendingVolumeFadeIn)
        {
            ApplyOutputVolume(Volume / 100d);
            return;
        }

        pendingVolumeFadeIn = false;
        CancelVolumeFade(false);
        var cancellation = new CancellationTokenSource();
        volumeFadeCancellation = cancellation;
        isVolumeFading = true;
        long generation = mediaGeneration;
        _ = RunVolumeFadeAsync(generation, cancellation);
    }

    private async Task RunVolumeFadeAsync(long generation, CancellationTokenSource cancellation)
    {
        // 使用平滑步进曲线，避免音量过渡开始和结束时出现突兀变化。
        CancellationToken token = cancellation.Token;
        try
        {
            double durationMilliseconds = Math.Max(100, Settings.VolumeFadeInDuration * 1000d);
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed.TotalMilliseconds < durationMilliseconds)
            {
                token.ThrowIfCancellationRequested();
                if (generation != mediaGeneration) return;
                double progress = Math.Clamp(stopwatch.Elapsed.TotalMilliseconds / durationMilliseconds, 0, 1);
                double eased = progress * progress * (3d - 2d * progress);
                ApplyOutputVolume(Volume / 100d * eased);
                await Task.Delay(16, token);
            }
            if (generation == mediaGeneration) ApplyOutputVolume(Volume / 100d);
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(volumeFadeCancellation, cancellation))
            {
                volumeFadeCancellation.Dispose();
                volumeFadeCancellation = null;
                isVolumeFading = false;
            }
        }
    }

    private void CancelVolumeFade(bool restoreTargetVolume)
    {
        CancellationTokenSource? cancellation = volumeFadeCancellation;
        volumeFadeCancellation = null;
        isVolumeFading = false;
        if (cancellation is not null)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }
        if (restoreTargetVolume) ApplyOutputVolume(Volume / 100d);
    }

    /// <summary>
    /// 把音量写入当前实际发声的引擎。音量渐入等过程必须走这里：
    /// libmpv 引擎模式下系统 MediaPlayer 没有媒体源，只写 MediaPlayer.Volume
    /// 相当于什么都没做，渐入会被静默跳过。
    /// </summary>
    private void ApplyOutputVolume(double value)
    {
        if (IsMpvEngineActive && mpvEngine is not null) mpvEngine.SetVolume(value);
        else MediaPlayer.Volume = value;
    }

    public IReadOnlyList<MediaTrackOption> GetAudioTrackOptions()
    {
        if (playbackItem is null) return [];
        var result = new List<MediaTrackOption>();
        for (int index = 0; index < playbackItem.AudioTracks.Count; index++)
        {
            AudioTrack track = playbackItem.AudioTracks[index];
            string name = string.IsNullOrWhiteSpace(track.Label) ? $"音轨 {index + 1}" : track.Label;
            if (!string.IsNullOrWhiteSpace(track.Language)) name += $" ({track.Language})";
            result.Add(new MediaTrackOption(index, name, playbackItem.AudioTracks.SelectedIndex == index));
        }
        return result;
    }

    public IReadOnlyList<MediaTrackOption> GetSubtitleTrackOptions()
    {
        // libmpv 引擎模式下字幕由引擎渲染：这里列出引擎的字幕轨，便于选择轨道或关闭字幕。
        if (IsMpvEngineActive && mpvEngine is not null)
        {
            mpvSubtitleTracks.Clear();
            var engineOptions = new List<MediaTrackOption>();
            foreach (MpvTrackInfo track in mpvEngine.GetTracks())
            {
                if (!string.Equals(track.Kind, "sub", StringComparison.OrdinalIgnoreCase)) continue;
                string name = string.IsNullOrWhiteSpace(track.Title)
                    ? (string.IsNullOrWhiteSpace(track.Language) ? $"字幕 {engineOptions.Count + 1}" : track.Language)
                    : track.Title;
                engineOptions.Add(new MediaTrackOption(engineOptions.Count, name,
                    track.Id == mpvEngine.CurrentSubtitleTrackId));
                mpvSubtitleTracks.Add(track);
            }
            return engineOptions;
        }

        if (playbackItem is null) return [];
        var result = new List<MediaTrackOption>();
        for (int index = 0; index < playbackItem.TimedMetadataTracks.Count; index++)
        {
            TimedMetadataTrack track = playbackItem.TimedMetadataTracks[index];
            if (track.TimedMetadataKind is not (TimedMetadataKind.Caption or
                TimedMetadataKind.Subtitle or TimedMetadataKind.ImageSubtitle) &&
                !TryGetPgsDocument(track, out _)) continue;
            string name = string.IsNullOrWhiteSpace(track.Label) ? $"字幕 {result.Count + 1}" : track.Label;
            if (!string.IsNullOrWhiteSpace(track.Language)) name += $" ({track.Language})";
            result.Add(new MediaTrackOption(index, name,
                ReferenceEquals(track, activeSubtitleTrack) ||
                string.Equals(track.Id, activeSubtitleTrack?.Id, StringComparison.Ordinal)));
        }
        return result;
    }

    /// <summary>
    /// 字幕菜单里"关闭字幕"项的勾选状态。
    /// libmpv 引擎：只有明确置为 <c>sid=no</c> 才算关闭（自动选择时读不到当前轨属于正常）；
    /// 系统媒体框架：没有任何轨道被选中即为关闭。
    /// </summary>
    public bool AreSubtitlesOff() =>
        IsMpvEngineActive && mpvEngine is not null
            ? mpvEngine.SubtitlesExplicitlyOff
            : GetSubtitleTrackOptions().All(track => !track.IsSelected);

    public async Task LoadExternalSubtitleAsync(StorageFile subtitleFile)
    {
        if (activeMediaSource is null || playbackItem is null || currentItem is null)
        {
            Notify("请先打开媒体文件", true);
            return;
        }

        long generation = mediaGeneration;
        bool added = await TryAddExternalSubtitleAsync(
            subtitleFile, activeMediaSource, generation, true);
        if (added) Notify($"正在加载外挂字幕：{subtitleFile.Name}");
    }

    private async Task AutoLoadExternalSubtitlesAsync(
        PlaylistItem mediaItem, MediaSource mediaSource, long generation)
    {
        try
        {
            StorageFolder? folder = await mediaItem.File.GetParentAsync();
            if (folder is null || generation != mediaGeneration ||
                !ReferenceEquals(mediaSource, activeMediaSource)) return;

            string mediaName = Path.GetFileNameWithoutExtension(mediaItem.Name);
            IReadOnlyList<StorageFile> files = await folder.GetFilesAsync();
            StorageFile[] candidates = files
                .Where(file => SupportedSubtitleExtensions.Contains(file.FileType) &&
                    IsMatchingSubtitleName(mediaName, Path.GetFileNameWithoutExtension(file.Name)))
                .OrderBy(file => file.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();

            bool activateFirst = true;
            foreach (StorageFile candidate in candidates)
            {
                if (generation != mediaGeneration ||
                    !ReferenceEquals(mediaSource, activeMediaSource)) return;
                bool added = await TryAddExternalSubtitleAsync(
                    candidate, mediaSource, generation, activateFirst);
                if (added) activateFirst = false;
            }

            if (candidates.Length > 0 && generation == mediaGeneration)
                Notify($"已发现 {loadedExternalSubtitlePaths.Count} 个外挂字幕");
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
        catch (Exception ex)
        {
            if (generation == mediaGeneration)
                Notify($"扫描外挂字幕失败：{ex.Message}", true);
        }
    }

    private async Task<bool> TryAddExternalSubtitleAsync(
        StorageFile subtitleFile, MediaSource mediaSource, long generation,
        bool activateWhenResolved)
    {
        AppLogService.Information("SubtitleLoadStarted", "开始加载外挂字幕", new
        {
            SubtitlePath = subtitleFile.Path,
            SubtitleType = subtitleFile.FileType,
            SubtitleSize = TryGetFileLength(subtitleFile.Path),
            MediaPath = currentItem?.Path,
            Generation = generation,
            ActivateWhenResolved = activateWhenResolved
        });
        if (generation != mediaGeneration || !ReferenceEquals(mediaSource, activeMediaSource) ||
            !SupportedSubtitleExtensions.Contains(subtitleFile.FileType) ||
            loadedExternalSubtitlePaths.Contains(subtitleFile.Path)) return false;

        try
        {
            if (subtitleFile.FileType.Equals(".srt", StringComparison.OrdinalIgnoreCase))
                return await AddSrtSubtitleAsync(
                    subtitleFile, mediaSource, generation, activateWhenResolved);
            if (subtitleFile.FileType.Equals(".sup", StringComparison.OrdinalIgnoreCase))
                return await AddPgsSubtitleAsync(
                    subtitleFile, mediaSource, generation, activateWhenResolved);

            TimedTextSource source;
            IRandomAccessStream? subtitleStream = null;
            IRandomAccessStream? indexStream = null;
            if (subtitleFile.FileType.Equals(".sub", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    subtitleStream = await subtitleFile.OpenReadAsync();
                    StorageFolder? folder = await subtitleFile.GetParentAsync();
                    if (folder is null) throw new FileNotFoundException("找不到字幕所在文件夹");
                    string indexName = Path.GetFileNameWithoutExtension(subtitleFile.Name) + ".idx";
                    StorageFile indexFile = await folder.GetFileAsync(indexName);
                    indexStream = await indexFile.OpenReadAsync();
                    source = TimedTextSource.CreateFromStreamWithIndex(subtitleStream, indexStream);
                }
                catch
                {
                    subtitleStream?.Dispose();
                    indexStream?.Dispose();
                    throw;
                }
            }
            else
            {
                subtitleStream = await subtitleFile.OpenReadAsync();
                source = TimedTextSource.CreateFromStream(subtitleStream);
            }

            string displayName = subtitleFile.Name;
            source.Resolved += (_, args) => dispatcherQueue.TryEnqueue(() =>
            {
                if (generation != mediaGeneration || playbackItem is null) return;
                if (args.Error is not null)
                {
                    loadedExternalSubtitlePaths.Remove(subtitleFile.Path);
                    AppLogService.Error("SubtitleResolveFailed", "系统字幕解析器返回错误", new
                    {
                        SubtitlePath = subtitleFile.Path,
                        SubtitleType = subtitleFile.FileType,
                        SubtitleSize = TryGetFileLength(subtitleFile.Path),
                        MediaPath = currentItem?.Path,
                        ErrorCode = args.Error.ErrorCode.ToString(),
                        ExtendedError = $"0x{args.Error.ExtendedError.HResult:X8}",
                        TrackCount = args.Tracks.Count
                    });
                    Notify($"外挂字幕加载失败：{displayName}；错误类型：" +
                        $"{args.Error.ErrorCode}，扩展错误：{args.Error.ExtendedError.HResult:X8}；" +
                        "详情已写入日志", true);
                    return;
                }

                for (int trackNumber = 0; trackNumber < args.Tracks.Count; trackNumber++)
                    args.Tracks[trackNumber].Label = args.Tracks.Count == 1
                        ? displayName : $"{displayName} ({trackNumber + 1})";

                if (!activateWhenResolved || args.Tracks.Count == 0) return;
                AppLogService.Information("SubtitleResolved", "外挂字幕解析成功", new
                {
                    SubtitlePath = subtitleFile.Path,
                    SubtitleType = subtitleFile.FileType,
                    TrackCount = args.Tracks.Count
                });
                TimedMetadataTrack resolvedTrack = args.Tracks[0];
                for (int index = 0; index < playbackItem.TimedMetadataTracks.Count; index++)
                {
                    if (!ReferenceEquals(playbackItem.TimedMetadataTracks[index], resolvedTrack)) continue;
                    SelectSubtitleTrack(index);
                    break;
                }
            });

            if (generation != mediaGeneration || !ReferenceEquals(mediaSource, activeMediaSource))
            {
                subtitleStream?.Dispose();
                indexStream?.Dispose();
                return false;
            }
            if (subtitleStream is not null) externalSubtitleStreams.Add(subtitleStream);
            if (indexStream is not null) externalSubtitleStreams.Add(indexStream);
            loadedExternalSubtitlePaths.Add(subtitleFile.Path);
            mediaSource.ExternalTimedTextSources.Add(source);
            return true;
        }
        catch (FileNotFoundException)
        {
            AppLogService.Error("SubtitleIndexMissing", "VobSub 缺少同名 IDX 索引", new
            {
                SubtitlePath = subtitleFile.Path,
                MediaPath = currentItem?.Path
            });
            Notify($"{subtitleFile.Name} 缺少同名 .idx 索引文件", true);
        }
        catch (Exception ex)
        {
            AppLogService.Error("SubtitleLoadException", "外挂字幕加载发生异常", new
            {
                SubtitlePath = subtitleFile.Path,
                SubtitleType = subtitleFile.FileType,
                SubtitleSize = TryGetFileLength(subtitleFile.Path),
                MediaPath = currentItem?.Path,
                Generation = generation
            }, ex);
            Notify($"外挂字幕加载失败：{ex.Message}", true);
        }
        return false;
    }

    private async Task<bool> AddPgsSubtitleAsync(
        StorageFile subtitleFile, MediaSource mediaSource, long generation,
        bool activateWhenAdded)
    {
        PgsSubtitleDocument document = await PgsSubtitleDocument.LoadAsync(subtitleFile);
        if (generation != mediaGeneration || !ReferenceEquals(mediaSource, activeMediaSource))
            return false;

        TimedMetadataTrack track = document.CreateTrack();
        pgsSubtitleDocuments[track] = document;
        loadedExternalSubtitlePaths.Add(subtitleFile.Path);
        mediaSource.ExternalTimedMetadataTracks.Add(track);
        AppLogService.Information("PgsParsed", "应用内 PGS 解析成功", new
        {
            SubtitlePath = subtitleFile.Path,
            SubtitleSize = TryGetFileLength(subtitleFile.Path),
            document.FrameCount,
            FirstStart = document.FirstStart,
            LastEnd = document.LastEnd
        });
        Notify($"已加载 PGS 字幕：{subtitleFile.Name}（{document.FrameCount} 帧）");
        if (activateWhenAdded)
        {
            activeSubtitleTrack = track;
            activePgsSubtitleDocument = document;
            activePgsFrameIndex = -2;
            hasLoggedFirstPgsFrame = false;
            UpdatePgsSubtitleForPosition(MediaPlayer.PlaybackSession.Position);
        }
        return true;
    }

    private async Task<bool> AddSrtSubtitleAsync(
        StorageFile subtitleFile, MediaSource mediaSource, long generation,
        bool activateWhenAdded)
    {
        string content;
        string encodingName;
        await using (Stream input = await subtitleFile.OpenStreamForReadAsync())
        using (var memory = new MemoryStream())
        {
            await input.CopyToAsync(memory);
            content = DecodeSubtitleText(memory.ToArray(), out encodingName);
        }

        var track = new TimedMetadataTrack(
            $"external-srt-{Guid.NewGuid():N}", string.Empty, TimedMetadataKind.Subtitle)
        {
            Label = subtitleFile.Name
        };

        int cueCount = 0;
        string normalized = content.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        foreach (string block in Regex.Split(normalized, @"\n\s*\n"))
        {
            string[] lines = block.Split('\n');
            int timingIndex = Array.FindIndex(lines, line => line.Contains("-->", StringComparison.Ordinal));
            if (timingIndex < 0) continue;

            string[] times = lines[timingIndex].Split("-->", 2, StringSplitOptions.TrimEntries);
            if (times.Length != 2 || !TryParseSrtTime(times[0], out TimeSpan start) ||
                !TryParseSrtTime(times[1], out TimeSpan end) || end <= start) continue;

            var cue = new TimedTextCue
            {
                Id = $"srt-{cueCount + 1}",
                StartTime = start,
                Duration = end - start
            };
            for (int lineIndex = timingIndex + 1; lineIndex < lines.Length; lineIndex++)
            {
                string line = CleanSrtText(lines[lineIndex]);
                if (line.Length > 0) cue.Lines.Add(new TimedTextLine { Text = line });
            }
            if (cue.Lines.Count == 0) continue;
            track.AddCue(cue);
            cueCount++;
        }

        if (cueCount == 0) throw new FormatException("没有找到有效的 SRT 时间轴");
        if (generation != mediaGeneration || !ReferenceEquals(mediaSource, activeMediaSource))
            return false;

        loadedExternalSubtitlePaths.Add(subtitleFile.Path);
        mediaSource.ExternalTimedMetadataTracks.Add(track);
        AppLogService.Information("SrtParsed", "应用内 SRT 解析成功", new
        {
            SubtitlePath = subtitleFile.Path,
            SubtitleSize = TryGetFileLength(subtitleFile.Path),
            CueCount = cueCount,
            DetectedEncoding = encodingName
        });
        if (activateWhenAdded)
            dispatcherQueue.TryEnqueue(() => ActivateSubtitleTrack(track, generation));
        return true;
    }

    private static string DecodeSubtitleText(byte[] bytes, out string encodingName)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            encodingName = "UTF-8 BOM";
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            encodingName = "UTF-16 LE";
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            encodingName = "UTF-16 BE";
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        try
        {
            encodingName = "UTF-8";
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            encodingName = "GB18030/GBK";
            return Encoding.GetEncoding(54936).GetString(bytes); // GB18030，兼容常见 GBK 字幕。
        }
    }

    private static long? TryGetFileLength(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : null; }
        catch { return null; }
    }

    private static bool TryParseSrtTime(string value, out TimeSpan result)
    {
        Match match = Regex.Match(value,
            @"(?<h>\d{1,3}):(?<m>\d{2}):(?<s>\d{2})[,.](?<ms>\d{1,3})");
        if (!match.Success)
        {
            result = default;
            return false;
        }

        int milliseconds = int.Parse(match.Groups["ms"].Value.PadRight(3, '0'));
        result = new TimeSpan(0, int.Parse(match.Groups["h"].Value),
            int.Parse(match.Groups["m"].Value), int.Parse(match.Groups["s"].Value), milliseconds);
        return true;
    }

    private static string CleanSrtText(string value)
    {
        string withoutTags = Regex.Replace(value, @"<[^>]+>|\{\\[^}]+\}", string.Empty);
        return WebUtility.HtmlDecode(withoutTags).Trim();
    }

    private void ActivateSubtitleTrack(TimedMetadataTrack track, long generation)
    {
        if (generation != mediaGeneration || playbackItem is null) return;
        for (int index = 0; index < playbackItem.TimedMetadataTracks.Count; index++)
        {
            if (!ReferenceEquals(playbackItem.TimedMetadataTracks[index], track)) continue;
            SelectSubtitleTrack(index);
            return;
        }
    }

    private static bool IsMatchingSubtitleName(string mediaName, string subtitleName) =>
        subtitleName.Equals(mediaName, StringComparison.OrdinalIgnoreCase) ||
        subtitleName.StartsWith(mediaName + ".", StringComparison.OrdinalIgnoreCase) ||
        subtitleName.StartsWith(mediaName + "_", StringComparison.OrdinalIgnoreCase);

    private bool TryGetPgsDocument(
        TimedMetadataTrack track, out PgsSubtitleDocument? document)
    {
        if (pgsSubtitleDocuments.TryGetValue(track, out document)) return true;
        foreach ((TimedMetadataTrack candidate, PgsSubtitleDocument value) in pgsSubtitleDocuments)
        {
            if (!string.Equals(candidate.Id, track.Id, StringComparison.Ordinal)) continue;
            document = value;
            return true;
        }
        document = null;
        return false;
    }

    private void UpdatePgsSubtitleForPosition(TimeSpan position)
    {
        PgsSubtitleDocument? document = activePgsSubtitleDocument;
        if (document is null) return;
        int frameIndex = document.FindFrameIndex(position);
        if (frameIndex == activePgsFrameIndex) return;
        activePgsFrameIndex = frameIndex;
        CancelPgsDecode();
        if (frameIndex < 0)
        {
            PgsSubtitleFrameChanged?.Invoke(null);
            return;
        }

        var cancellation = new CancellationTokenSource();
        pgsDecodeCancellation = cancellation;
        long generation = mediaGeneration;
        _ = DecodePgsFrameAsync(document, frameIndex, generation, cancellation);
    }

    private async Task DecodePgsFrameAsync(PgsSubtitleDocument document, int frameIndex,
        long generation, CancellationTokenSource cancellation)
    {
        try
        {
            PgsSubtitleImage? image = await Task.Run(
                () => document.Decode(frameIndex), cancellation.Token);
            if (cancellation.IsCancellationRequested || generation != mediaGeneration ||
                !ReferenceEquals(document, activePgsSubtitleDocument) ||
                frameIndex != activePgsFrameIndex) return;
            if (!hasLoggedFirstPgsFrame && image is not null)
            {
                hasLoggedFirstPgsFrame = true;
                int nonTransparentPixels = 0;
                byte maximumAlpha = 0;
                for (int offset = 3; offset < image.Pixels.Length; offset += 4)
                {
                    byte alpha = image.Pixels[offset];
                    if (alpha > 0) nonTransparentPixels++;
                    if (alpha > maximumAlpha) maximumAlpha = alpha;
                }
                AppLogService.Information("PgsFrameDecoded", "首个 PGS 字幕帧解码成功", new
                {
                    FrameIndex = frameIndex,
                    image.Width,
                    image.Height,
                    image.X,
                    image.Y,
                    image.CanvasWidth,
                    image.CanvasHeight,
                    NonTransparentPixels = nonTransparentPixels,
                    MaximumAlpha = maximumAlpha
                });
            }
            dispatcherQueue.TryEnqueue(() => PgsSubtitleFrameChanged?.Invoke(image));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLogService.Error("PgsFrameDecodeFailed", "PGS 字幕帧解码失败",
                new { document.Label, FrameIndex = frameIndex }, ex);
        }
    }

    private void ClearExternalSubtitleState()
    {
        loadedExternalSubtitlePaths.Clear();
        CancelPgsDecode();
        pgsSubtitleDocuments.Clear();
        activePgsSubtitleDocument = null;
        activePgsFrameIndex = -2;
        hasLoggedFirstPgsFrame = false;
        PgsSubtitleFrameChanged?.Invoke(null);
        foreach (IRandomAccessStream stream in externalSubtitleStreams) stream.Dispose();
        externalSubtitleStreams.Clear();
        activeMediaSource = null;
    }

    public void SelectAudioTrack(int index)
    {
        if (playbackItem is null || index < 0 || index >= playbackItem.AudioTracks.Count) return;
        playbackItem.AudioTracks.SelectedIndex = index;
        ShowChrome();
        AudioTrack track = playbackItem.AudioTracks[index];
        string name = string.IsNullOrWhiteSpace(track.Label) ? $"音轨 {index + 1}" : track.Label;
        Notify($"已切换到{name}");
    }

    public void SelectSubtitleTrack(int index)
    {
        // libmpv 引擎模式下由引擎渲染字幕：index < 0 关闭字幕，否则切换到对应引擎字幕轨。
        if (IsMpvEngineActive && mpvEngine is not null)
        {
            try
            {
                if (index >= 0 && index < mpvSubtitleTracks.Count)
                {
                    MpvTrackInfo engineTrack = mpvSubtitleTracks[index];
                    mpvEngine.SetSubtitleTrack(engineTrack.Id);
                    // 记住选择：切换媒体后自动重选同一轨。
                    Settings.EngineSubtitlesOff = false;
                    Settings.EngineSubtitlePreference = string.IsNullOrWhiteSpace(engineTrack.Title)
                        ? engineTrack.Language : engineTrack.Title;
                    Settings.EngineSubtitleOrdinal = index + 1;
                    persistSettings();
                    ShowChrome();
                    Notify("已切换字幕轨道（已记住该选择）");
                }
                else
                {
                    mpvEngine.SetSubtitleTrack(0);
                    Settings.EngineSubtitlesOff = true;
                    persistSettings();
                    ShowChrome();
                    Notify("已关闭字幕（切换媒体后仍保持关闭）");
                }
            }
            catch (Exception ex)
            {
                AppLogService.Warning("MpvSubtitleTrackFailed", "切换 libmpv 字幕轨失败",
                    new { Index = index }, ex);
            }
            return;
        }

        // 文本字幕由 XAML 浮层绘制，图片字幕仍交由系统播放组件呈现。
        if (playbackItem is null) return;
        DetachSubtitleTrack();
        if (index < 0)
        {
            ShowChrome();
            Notify("已关闭字幕");
            return;
        }
        if (index >= playbackItem.TimedMetadataTracks.Count) return;

        TimedMetadataTrack track = playbackItem.TimedMetadataTracks[index];
        activeSubtitleTrack = track;
        if (TryGetPgsDocument(track, out PgsSubtitleDocument? pgsDocument))
        {
            activePgsSubtitleDocument = pgsDocument;
            activePgsFrameIndex = -2;
            hasLoggedFirstPgsFrame = false;
            playbackItem.TimedMetadataTracks.SetPresentationMode((uint)index,
                TimedMetadataTrackPresentationMode.Hidden);
            UpdatePgsSubtitleForPosition(MediaPlayer.PlaybackSession.Position);
        }
        else if (track.TimedMetadataKind == TimedMetadataKind.ImageSubtitle)
        {
            playbackItem.TimedMetadataTracks.SetPresentationMode((uint)index,
                TimedMetadataTrackPresentationMode.PlatformPresented);
        }
        else
        {
            track.CueEntered += SubtitleTrack_CueEntered;
            track.CueExited += SubtitleTrack_CueExited;
            playbackItem.TimedMetadataTracks.SetPresentationMode((uint)index,
                TimedMetadataTrackPresentationMode.ApplicationPresented);
        }
        ShowChrome();
        string name = string.IsNullOrWhiteSpace(track.Label) ? "所选字幕" : track.Label;
        Notify($"已启用{name}");
    }

    private void DetachSubtitleTrack()
    {
        if (playbackItem is not null)
        {
            for (uint index = 0; index < playbackItem.TimedMetadataTracks.Count; index++)
                playbackItem.TimedMetadataTracks.SetPresentationMode(index,
                    TimedMetadataTrackPresentationMode.Disabled);
        }
        if (activeSubtitleTrack is not null)
        {
            activeSubtitleTrack.CueEntered -= SubtitleTrack_CueEntered;
            activeSubtitleTrack.CueExited -= SubtitleTrack_CueExited;
            activeSubtitleTrack.CueEntered -= PgsSubtitleTrack_CueEntered;
            activeSubtitleTrack.CueExited -= PgsSubtitleTrack_CueExited;
            activeSubtitleTrack = null;
        }
        CancelPgsDecode();
        activePgsSubtitleDocument = null;
        activePgsFrameIndex = -2;
        hasLoggedFirstPgsFrame = false;
        PgsSubtitleFrameChanged?.Invoke(null);
        SubtitleText = string.Empty;
    }

    private void SubtitleTrack_CueEntered(TimedMetadataTrack sender, MediaCueEventArgs args)
    {
        // 引擎模式下字幕由 libmpv 渲染：忽略系统会话可能残留的字幕回调，避免两套字幕叠加。
        if (IsMpvEngineActive) return;
        if (args.Cue is not TimedTextCue cue) return;
        string text = string.Join(Environment.NewLine, cue.Lines.Select(line => line.Text));
        dispatcherQueue.TryEnqueue(() =>
        {
            if (IsMpvEngineActive || Settings.HideOwnSubtitles) return;
            SubtitleText = text;
        });
    }

    private void SubtitleTrack_CueExited(TimedMetadataTrack sender, MediaCueEventArgs args) =>
        dispatcherQueue.TryEnqueue(() =>
        {
            if (IsMpvEngineActive) return;
            SubtitleText = string.Empty;
        });

    private async void PgsSubtitleTrack_CueEntered(TimedMetadataTrack sender, MediaCueEventArgs args)
    {
        // 引擎模式下由 libmpv 渲染字幕（含 PGS）；关闭“本项目字幕”时同样不再自绘图片字幕。
        if (IsMpvEngineActive || Settings.HideOwnSubtitles) return;
        if (args.Cue is not DataCue cue || !cue.Id.StartsWith("pgs:", StringComparison.Ordinal) ||
            !int.TryParse(cue.Id.AsSpan(4), out int frameIndex) ||
            !pgsSubtitleDocuments.TryGetValue(sender, out PgsSubtitleDocument? document)) return;

        CancelPgsDecode();
        var cancellation = new CancellationTokenSource();
        pgsDecodeCancellation = cancellation;
        long generation = mediaGeneration;
        try
        {
            PgsSubtitleImage? image = await Task.Run(() => document.Decode(frameIndex), cancellation.Token);
            if (cancellation.IsCancellationRequested || generation != mediaGeneration ||
                !ReferenceEquals(sender, activeSubtitleTrack)) return;
            dispatcherQueue.TryEnqueue(() => PgsSubtitleFrameChanged?.Invoke(image));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLogService.Error("PgsFrameDecodeFailed", "PGS 字幕帧解码失败",
                new { document.Label, FrameIndex = frameIndex }, ex);
        }
    }

    private void PgsSubtitleTrack_CueExited(TimedMetadataTrack sender, MediaCueEventArgs args)
    {
        CancelPgsDecode();
        dispatcherQueue.TryEnqueue(() => PgsSubtitleFrameChanged?.Invoke(null));
    }

    private void CancelPgsDecode()
    {
        CancellationTokenSource? cancellation = pgsDecodeCancellation;
        pgsDecodeCancellation = null;
        if (cancellation is null) return;
        cancellation.Cancel();
        cancellation.Dispose();
    }

    private void PlayPrevious()
    {
        if (Playlist.Count == 0) return;
        int index = Math.Max(0, Playlist.IndexOf(SelectedItem!) - 1);
        SelectedItem = Playlist[index];
    }

    private void PlayNext()
    {
        if (Playlist.Count == 0) return;
        int current = Playlist.IndexOf(SelectedItem!);
        SelectedItem = Playlist[(current + 1 + Playlist.Count) % Playlist.Count];
    }

    /// <summary>
    /// 自动续播下一项（播放结束、跳过片尾时调用）。隐私打开的文件不在播放列表里，
    /// 若继续走 PlayNext 会跳到列表第 0 项，与“不改动播放列表”的意图冲突，因此此时不续播。
    /// 用户手动点击“下一项”不受影响。
    /// </summary>
    private void PlayNextAutomatically()
    {
        if (currentItem is not null && !Playlist.Contains(currentItem)) return;
        PlayNext();
    }

    private void PlayPause()
    {
        if (IsMpvEngineActive && mpvEngine is not null)
        {
            if (mpvEngine.IsPaused) mpvEngine.Play();
            else mpvEngine.Pause();
            PlayPauseGlyph = mpvEngine.IsPaused ? "\uE768" : "\uE769";
            IsPlaying = !mpvEngine.IsPaused;
            PausedOverlayVisibility = mpvEngine.IsPaused ? Visibility.Visible : Visibility.Collapsed;
            ShowChrome();
            return;
        }

        if (currentItem is null || MediaPlayer.Source is null)
        {
            ShowChrome();
            Notify("当前没有打开的媒体文件");
            return;
        }

        if (MediaPlayer.PlaybackSession.PlaybackState == MediaPlaybackState.Playing) MediaPlayer.Pause();
        else
        {
            MediaPlayer.Play();
            StartPendingVolumeFade();
        }
        ShowChrome();
    }

    private void SeekBy(TimeSpan amount)
    {
        if (IsMpvEngineActive && mpvEngine is not null)
        {
            mpvEngine.SeekRelative(amount.TotalSeconds);
            ShowChrome();
            Notify(amount.TotalSeconds < 0
                ? $"后退 {Math.Abs(amount.TotalSeconds):0} 秒"
                : $"前进 {amount.TotalSeconds:0} 秒");
            return;
        }

        var session = MediaPlayer.PlaybackSession;
        var target = session.Position + amount;
        if (target < TimeSpan.Zero) target = TimeSpan.Zero;
        if (session.NaturalDuration > TimeSpan.Zero && target > session.NaturalDuration) target = session.NaturalDuration;
        session.Position = target;
        ShowChrome();
        Notify(amount.TotalSeconds < 0
            ? $"后退 {Math.Abs(amount.TotalSeconds):0} 秒"
            : $"前进 {amount.TotalSeconds:0} 秒");
    }

    private void PositionTimer_Tick(object? sender, object e)
    {
        if (isSeeking) return;

        // libmpv 引擎：位置/时长/暂停状态由引擎提供。
        if (IsMpvEngineActive && mpvEngine is not null)
        {
            double engineDuration = mpvEngine.Duration;
            double enginePosition = mpvEngine.Position;
            if (engineDuration > 0)
            {
                Duration = engineDuration;
                DurationText = FormatTime(TimeSpan.FromSeconds(engineDuration));
            }
            Position = enginePosition;
            CurrentTime = FormatTime(TimeSpan.FromSeconds(enginePosition));
            bool paused = mpvEngine.IsPaused;
            IsPlaying = !paused;
            PlayPauseGlyph = paused ? "\uE768" : "\uE769";
            PausedOverlayVisibility = paused ? Visibility.Visible : Visibility.Collapsed;

            // 与系统会话路径保持一致：播放中每 5 秒记录一次进度，异常退出也不会丢进度。
            if (Settings.SavePlaybackPosition && currentItem is not null && !paused &&
                DateTime.UtcNow - lastHistoryWriteUtc >= TimeSpan.FromSeconds(5))
            {
                lastHistoryWriteUtc = DateTime.UtcNow;
                SaveCurrentPosition();
            }

            // 片尾跳过同样适用于引擎模式（与系统会话路径同一套语义）。
            if (!isSkippingOutro && Settings.SkipOutroSeconds > 0 && engineDuration > 0 &&
                engineDuration - enginePosition <= Settings.SkipOutroSeconds)
            {
                isSkippingOutro = true;
                if (currentItem is not null)
                {
                    playbackHistory.Clear(currentItem.Path);
                    currentItem.ApplyPlaybackHistory(null);
                    _ = playbackHistory.FlushAsync();
                }
                if (Settings.AutoPlay) PlayNextAutomatically(); else mpvEngine.Pause();
            }
            return;
        }

        try
        {
            PollPlaybackSession();
            playbackSessionUnavailableLogged = false;
        }
        catch (COMException ex)
        {
            // 切换/关闭媒体源或解码引擎被回收时，会话可能短暂不可用（RPC 断开）。
            // 跳过本次轮询即可；新媒体源就绪后下一次 tick 自动恢复。
            if (!playbackSessionUnavailableLogged)
            {
                playbackSessionUnavailableLogged = true;
                AppLogService.Warning("PlaybackSessionPollFailed", "媒体会话暂不可用，跳过进度轮询", exception: ex);
            }
        }
    }

    private void PollPlaybackSession()
    {
        MediaPlaybackSession session = MediaPlayer.PlaybackSession;
        Position = session.Position.TotalSeconds;
        CurrentTime = FormatTime(session.Position);
        UpdatePgsSubtitleForPosition(session.Position);

        if (Settings.SavePlaybackPosition && currentItem is not null &&
            session.PlaybackState == MediaPlaybackState.Playing &&
            DateTime.UtcNow - lastHistoryWriteUtc >= TimeSpan.FromSeconds(5))
        {
            lastHistoryWriteUtc = DateTime.UtcNow;
            SaveCurrentPosition();
        }

        double remaining = session.NaturalDuration.TotalSeconds - session.Position.TotalSeconds;
        if (!isSkippingOutro && Settings.SkipOutroSeconds > 0 &&
            session.NaturalDuration > TimeSpan.Zero && remaining <= Settings.SkipOutroSeconds)
        {
            isSkippingOutro = true;
            if (currentItem is not null)
            {
                playbackHistory.Clear(currentItem.Path);
                currentItem.ApplyPlaybackHistory(null);
                _ = playbackHistory.FlushAsync();
            }
            if (Settings.AutoPlay) PlayNextAutomatically(); else MediaPlayer.Pause();
        }
    }

    private void ChromeTimer_Tick(object? sender, object e)
    {
        chromeTimer.Stop();
        // 用实际播放状态判断：引擎模式下系统会话没有媒体源，
        // 只读 PlaybackState 会让控制层在引擎播放时永远不淡出。
        if (IsPlaying) ChromeOpacity = 0;
    }

    private void MediaPlayer_MediaOpened(MediaPlayer sender, object args) => dispatcherQueue.TryEnqueue(() =>
    {
        // 停止操作可能发生在 MediaOpened 回调排队之后；此时不得重新启动旧媒体。
        if (currentItem is null || playbackItem is null || sender.Source is null) return;
        // 媒体回调不保证在 UI 线程触发，因此所有绑定属性更新都加入调度队列。
        var naturalDuration = sender.PlaybackSession.NaturalDuration;
        Duration = Math.Max(1, naturalDuration.TotalSeconds);
        DurationText = FormatTime(naturalDuration);
        MediaWidth = sender.PlaybackSession.NaturalVideoWidth;
        MediaHeight = sender.PlaybackSession.NaturalVideoHeight;
        OnPropertyChanged(nameof(OutroMarkerText));
        double target = Math.Max(Settings.SkipIntroSeconds, pendingResumePosition);
        if (sender.PlaybackSession.CanSeek && target > 0 && target < naturalDuration.TotalSeconds - 5)
            sender.PlaybackSession.Position = TimeSpan.FromSeconds(target);
        pendingResumePosition = 0;
        if (Settings.AutoPlay || forcePlayOnMediaOpened)
        {
            sender.Play();
            StartPendingVolumeFade();
        }
        forcePlayOnMediaOpened = false;
        if (Settings.AutoFullScreen) enterFullScreen();
    });
    private void MediaPlayer_MediaEnded(MediaPlayer sender, object args) => dispatcherQueue.TryEnqueue(() =>
    {
        // 已执行停止并关闭时，忽略旧媒体源排队中的结束事件。
        if (currentItem is null || playbackItem is null || sender.Source is null) return;
        playbackHistory.Clear(currentItem.Path);
        currentItem.ApplyPlaybackHistory(null);
        _ = playbackHistory.FlushAsync();
        if (Settings.AutoPlay) PlayNextAutomatically();
    });
    private void MediaPlayer_MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args) => dispatcherQueue.TryEnqueue(() =>
    {
        if (currentItem is null || playbackItem is null || sender.Source is null) return;
        AppLogService.Error("MediaPlaybackFailed", "媒体播放失败", new
        {
            MediaPath = currentItem.Path,
            args.Error,
            args.ExtendedErrorCode,
            args.ErrorMessage
        });
        CancelVolumeFade(true);
        pendingVolumeFadeIn = false;
        PausedOverlayVisibility = Visibility.Collapsed;
        IsPlaying = false;
        Title = $"播放失败：{args.ErrorMessage}";
    });
    private void PlaybackSession_PlaybackStateChanged(MediaPlaybackSession sender, object args) => dispatcherQueue.TryEnqueue(() =>
    {
        // libmpv 引擎模式下系统会话没有媒体源，它的状态不能代表当前播放状态，
        // 否则会显示与实际不符的暂停角标。
        if (IsMpvEngineActive) return;
        IsPlaying = sender.PlaybackState == MediaPlaybackState.Playing;
        PlayPauseGlyph = IsPlaying ? "\uE769" : "\uE768";
        PausedOverlayVisibility = sender.PlaybackState == MediaPlaybackState.Paused && currentItem is not null
            ? Visibility.Visible : Visibility.Collapsed;
    });
    private static string FormatTime(TimeSpan value) => value.TotalHours >= 1 ? value.ToString(@"hh\:mm\:ss") : value.ToString(@"mm\:ss");

    public void Dispose()
    {
        // 释放 WinRT 播放与合成资源之前，先保存用户可见状态。
        SaveCurrentPosition(true);
        StopMpvPlayback();
        playlistPersistence.Save(Playlist.Select(item => item.Path), Folders);
        DetachSubtitleTrack();
        ClearExternalSubtitleState();
        CancelVolumeFade(false);
        positionTimer.Stop();
        chromeTimer.Stop();
        playlistHistoryResolutionCancellation?.Cancel();
        playlistHistoryResolutionCancellation?.Dispose();
        playbackHistory.Dispose();
        MediaPlayer.Dispose();
    }

    private void SaveCurrentPosition(bool flushImmediately = false)
    {
        if (!Settings.SavePlaybackPosition || currentItem is null || isSkippingOutro ||
            string.Equals(historySuppressedForPath, currentItem.Path,
                StringComparison.OrdinalIgnoreCase)) return;

        // 播放位置必须按当前实际播放的引擎读取：libmpv 引擎模式下系统会话没有媒体源，
        // 若继续读会话会得到 0/0，而 0 时长的记录会被当作无效并删除，
        // 结果是引擎模式播放过的内容进度丢失、重启后也回不到上次播放的内容。
        double position;
        double duration;
        if (IsMpvEngineActive && mpvEngine is not null)
        {
            position = mpvEngine.Position;
            duration = mpvEngine.Duration;
        }
        else
        {
            MediaPlaybackSession session = MediaPlayer.PlaybackSession;
            position = session.Position.TotalSeconds;
            duration = session.NaturalDuration.TotalSeconds;
        }

        // 时长未知（媒体尚未装载完成）时不写记录，避免把已有进度当作无效记录清掉。
        if (duration <= 0) return;

        playbackHistory.Update(currentItem.Path, position, duration, Settings.MaxPlaybackHistoryEntries);
        ApplyPlaybackHistory(currentItem);
        if (flushImmediately) _ = playbackHistory.FlushAsync();
        else _ = playbackHistory.FlushIfDueAsync(TimeSpan.FromSeconds(30));
    }
}
