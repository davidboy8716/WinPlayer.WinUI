using System;
using System.IO;
using System.Runtime.InteropServices;

namespace WinPlayer.WinUI;

/// <summary>
/// 统一管理当前窗口鼠标指针的显示与隐藏。
///
/// 背景：Windows App SDK 2.2 / InteractiveExperiences 2.0 中，客户端指针由输入栈在
/// 独立于 UI 线程的上下文中持续管理：UI 线程上的 ShowCursor/SetCursor/WM_SETCURSOR，
/// 以及 UIElement.ProtectedCursor 都不会改变实际显示（箭头句柄恒为系统标准箭头并被
/// 逐帧重设）。
///
/// 因此本类采用唯一有效的 OS 级手段：隐藏时用 SetSystemCursor 把**全部系统光标形状**
/// （箭头、文本 I 型、手型、调整大小、十字、忙碌等，见 <see cref="SystemCursorIds"/>）
/// 临时替换为全透明光标（Cursors/blank.cur）。此后无论哪条线程绘制哪种形状，画出来的
/// 都是透明 → 指针在任何形态下都不可见；恢复时用 SPI_SETCURSORS 按当前主题重新加载
/// 整套系统光标。替换期间若指针离开本窗口（移到其他程序/任务栏），会自动临时还原，
/// 避免把别的程序的指针也变没；指针回到本窗口再恢复隐藏。
///
/// 注意：只替换"标准箭头"是不够的——指针移到文本框、链接、调整大小区域时会切成别的
/// 形状，那些形状仍是可见的。该方案影响的是系统光标的显示映射，是本栈上唯一能真正隐藏
/// 指针的途径；隐藏期间若进程异常退出，可在系统"鼠标指针设置"里点"恢复默认"还原。
/// </summary>
public sealed class CursorManager
{
    // ---- 常量 ----
    /// <summary>
    /// 需要一并替换为透明光标的系统光标形状。取值即 Win32 的 OCR_* 常量。
    /// </summary>
    private static readonly uint[] SystemCursorIds =
    {
        32512, // OCR_NORMAL       标准箭头
        32513, // OCR_IBEAM        文本输入
        32514, // OCR_WAIT         忙碌
        32515, // OCR_CROSS        十字
        32516, // OCR_UP           向上箭头
        32640, // OCR_SIZE         尺寸（旧式）
        32641, // OCR_ICON         图标
        32642, // OCR_SIZENWSE     斜向调整（左上-右下）
        32643, // OCR_SIZENESW     斜向调整（右上-左下）
        32644, // OCR_SIZEWE       水平调整
        32645, // OCR_SIZENS       垂直调整
        32646, // OCR_SIZEALL      移动
        32647, // OCR_ICOCUR       图标光标
        32648, // OCR_NO           禁止
        32649, // OCR_HAND         手型/链接
        32650, // OCR_APPSTARTING  后台启动
        32651, // OCR_HELP         帮助
    };

    private const uint SPI_SETCURSORS = 0x0057;

    // ---- Win32 ----
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadCursorFromFile(string fileName);

    [DllImport("user32.dll")]
    private static extern bool SetSystemCursor(IntPtr hcur, uint id);

    [DllImport("user32.dll")]
    private static extern bool DestroyCursor(IntPtr hCursor);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private readonly Func<IntPtr> windowHandle; // 主窗口 HWND
    private bool hidden;
    private bool cursorsSwappedToBlank;

    public bool IsHidden => hidden;

    public CursorManager(Func<IntPtr> windowHandle)
    {
        this.windowHandle = windowHandle;
    }

    /// <summary>隐藏指针：把全部系统光标形状替换为透明光标。</summary>
    public void Hide()
    {
        hidden = true;
        EnsureHiddenState();
    }

    /// <summary>恢复显示：按当前主题还原整套系统光标。</summary>
    public void Show()
    {
        hidden = false;
        RestoreSystemCursors();
    }

    /// <summary>
    /// 隐藏期间由 UI 线程计时器周期调用（约每 100ms）：
    /// 指针在本窗口内 → 确保各形状都是透明替换态；指针已离开本窗口 → 临时还原系统光标，
    /// 以免影响其他程序的显示；指针回到窗口后再次替换。
    /// </summary>
    public void KeepHidden()
    {
        if (!hidden) return;
        EnsureHiddenState();
    }

    private void EnsureHiddenState()
    {
        try
        {
            if (PointerInsideThisWindow())
                SwapSystemCursorsToBlank();
            else
                RestoreSystemCursors();
        }
        catch
        {
            // 出现异常时保持现状，下个周期自动重试。
        }
    }

    private bool PointerInsideThisWindow()
    {
        if (!GetCursorPos(out POINT point)) return true; // 取不到时按“在窗口内”处理（保守）
        if (!GetWindowRect(windowHandle(), out RECT rect)) return true;
        return point.X >= rect.Left && point.X < rect.Right &&
               point.Y >= rect.Top && point.Y < rect.Bottom;
    }

    private void SwapSystemCursorsToBlank()
    {
        if (cursorsSwappedToBlank) return;
        string curPath = Path.Combine(AppContext.BaseDirectory, "blank.cur");
        if (!File.Exists(curPath)) return; // 缺少资源：无法隐藏（功能降级）

        // 逐个形状替换：SetSystemCursor 会接管并销毁传入的光标句柄，
        // 因此每种形状都必须重新从文件加载一份，复用同一句柄会因句柄已被销毁而失败。
        bool anySwapped = false;
        foreach (uint id in SystemCursorIds)
        {
            IntPtr cursor = LoadCursorFromFile(curPath);
            if (cursor == IntPtr.Zero) continue;
            if (SetSystemCursor(cursor, id)) anySwapped = true;
            else DestroyCursor(cursor); // 未被系统接管时自行释放，避免句柄泄漏
        }
        cursorsSwappedToBlank = anySwapped;
    }

    private void RestoreSystemCursors()
    {
        if (!cursorsSwappedToBlank) return;
        try
        {
            // 让系统按当前主题重新加载整套标准光标（箭头、I 型、手型、调整大小等）。
            SystemParametersInfo(SPI_SETCURSORS, 0, IntPtr.Zero, 0);
        }
        catch
        {
        }
        // 替换用的句柄已交给系统接管并销毁，这里只需复位状态。
        cursorsSwappedToBlank = false;
    }
}
