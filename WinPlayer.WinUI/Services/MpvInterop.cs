using System;
using System.Runtime.InteropServices;

namespace WinPlayer.WinUI.Services;

/// <summary>
/// libmpv 客户端与渲染 API 的原始绑定（libmpv-2.dll，x64，cdecl 调用约定）。
/// 常量取值与 include/mpv/client.h、include/mpv/render.h 一致。
/// </summary>
internal static class LibMpv
{
    private const string Dll = "libmpv-2.dll";

    // ---- client.h ----
    public const int MPV_FORMAT_NONE = 0;
    public const int MPV_FORMAT_STRING = 1;
    public const int MPV_FORMAT_FLAG = 3;
    public const int MPV_FORMAT_INT64 = 4;
    public const int MPV_FORMAT_DOUBLE = 5;

    public const int MPV_EVENT_NONE = 0;
    public const int MPV_EVENT_SHUTDOWN = 1;
    public const int MPV_EVENT_LOG_MESSAGE = 2;
    public const int MPV_EVENT_END_FILE = 7;
    public const int MPV_EVENT_FILE_LOADED = 8;
    public const int MPV_EVENT_VIDEO_RECONFIG = 17;
    public const int MPV_EVENT_SEEK = 20;
    public const int MPV_EVENT_PLAYBACK_RESTART = 21;

    public const int MPV_ERROR_UNINITIALIZED = -9;

    // ---- render.h ----
    public const int MPV_RENDER_PARAM_INVALID = 0;
    public const int MPV_RENDER_PARAM_API_TYPE = 1;
    public const int MPV_RENDER_PARAM_BLOCK_FOR_TARGET_TIME = 12;
    public const int MPV_RENDER_PARAM_SKIP_RENDERING = 13;
    public const int MPV_RENDER_PARAM_SW_SIZE = 17;
    public const int MPV_RENDER_PARAM_SW_FORMAT = 18;
    public const int MPV_RENDER_PARAM_SW_STRIDE = 19;
    public const int MPV_RENDER_PARAM_SW_POINTER = 20;

    public const ulong MPV_RENDER_UPDATE_FRAME = 1UL << 0;
    public const string MPV_RENDER_API_TYPE_SW = "sw";

    [StructLayout(LayoutKind.Sequential)]
    public struct MpvRenderParam
    {
        public int type;
        public IntPtr data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MpvEvent
    {
        public int event_id;
        public int error;
        public ulong reply_userdata;
        public IntPtr data;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void MpvRenderUpdateFn(IntPtr callbackContext);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr mpv_create();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_initialize(IntPtr context);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_set_option_string(IntPtr context,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_set_property_string(IntPtr context,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_command(IntPtr context, IntPtr args);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr mpv_wait_event(IntPtr context, double timeout);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr mpv_error_string(int error);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_get_property(IntPtr context,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format, IntPtr data);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr mpv_get_property_string(IntPtr context,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mpv_free(IntPtr data);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mpv_terminate_destroy(IntPtr context);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_render_context_create(out IntPtr renderContext, IntPtr context, IntPtr parameters);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mpv_render_context_set_update_callback(IntPtr renderContext,
        MpvRenderUpdateFn callback, IntPtr callbackContext);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern ulong mpv_render_context_update(IntPtr renderContext);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_render_context_render(IntPtr renderContext, IntPtr parameters);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mpv_render_context_free(IntPtr renderContext);

    /// <summary>由 <see cref="MpvDvEngine"/> 在加载动态库时设置。</summary>
    public static string? ResolvedPath { get; set; }

    public static string ErrorString(int error)
    {
        IntPtr pointer = mpv_error_string(error);
        return pointer == IntPtr.Zero ? $"error {error}" : Marshal.PtrToStringAnsi(pointer) ?? $"error {error}";
    }

    /// <summary>按 argv 形式执行 mpv 命令（UTF-8，不经 shell 解析）。</summary>
    public static int Command(IntPtr context, params string[] args)
    {
        var pointers = new IntPtr[args.Length + 1];
        for (int i = 0; i < args.Length; i++) pointers[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
        pointers[args.Length] = IntPtr.Zero;

        IntPtr array = Marshal.AllocHGlobal(IntPtr.Size * pointers.Length);
        try
        {
            Marshal.Copy(pointers, 0, array, pointers.Length);
            return mpv_command(context, array);
        }
        finally
        {
            Marshal.FreeHGlobal(array);
            for (int i = 0; i < args.Length; i++) Marshal.FreeCoTaskMem(pointers[i]);
        }
    }

    public static double? GetDouble(IntPtr context, string name)
    {
        IntPtr buffer = Marshal.AllocHGlobal(8);
        try
        {
            if (mpv_get_property(context, name, MPV_FORMAT_DOUBLE, buffer) < 0) return null;
            return BitConverter.Int64BitsToDouble(Marshal.ReadInt64(buffer));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static long? GetInt64(IntPtr context, string name)
    {
        IntPtr buffer = Marshal.AllocHGlobal(8);
        try
        {
            if (mpv_get_property(context, name, MPV_FORMAT_INT64, buffer) < 0) return null;
            return Marshal.ReadInt64(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static string? GetString(IntPtr context, string name)
    {
        IntPtr pointer = mpv_get_property_string(context, name);
        if (pointer == IntPtr.Zero) return null;
        try
        {
            return Marshal.PtrToStringUTF8(pointer);
        }
        finally
        {
            mpv_free(pointer);
        }
    }
}
