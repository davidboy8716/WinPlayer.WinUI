using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.DirectX;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using WinPlayer.WinUI.Effects;
using WinPlayer.WinUI.Models;
using WinPlayer.WinUI.Services;
using WinPlayer.WinUI.ViewModels;

namespace WinPlayer.WinUI;

/// <summary>
/// 承载 WinUI 可视化树，并协调窗口输入、合成渲染、悬浮面板动画、
/// 对话框以及本机 AppWindow 行为。
/// </summary>
public sealed partial class MainWindow : Window
{
    private static readonly PropertyInfo? ProtectedCursorProperty = typeof(UIElement).GetProperty(
        "ProtectedCursor", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    private static readonly InputCursor HorizontalResizeCursor =
        InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    private bool isFullScreen;
    // 全屏鼠标自动隐藏状态：窗口激活期间鼠标停止操作超过延时后隐藏光标，
    // 一旦移动/点击/滚动立即恢复显示并重新计时。
    private bool windowActive = true;
    private bool fullScreenCursorHidden;
    // 是否处于“正在播放”状态：只有在全屏且正在播放时才自动隐藏指针，
    // 全屏暂停/停止时指针保持可见。
    private bool isPlaybackActive;
    // “窗口总在最前”开关状态（全屏期间暂存，退出全屏后生效）。
    private bool keepOnTopRequested;
    private DateTime cursorHideDeadline = DateTime.MaxValue;
    private NativePoint cursorActivityAnchor;
    private bool cursorActivityAnchorSet;
    private readonly CursorManager cursorManager;
    private bool isDraggingPosition;
    private bool isDraggingWindow;
    private bool windowDragMoved;
    private bool windowDragStartedInPlaybackArea;
    private FrameworkElement? activeSidebarHandle;
    private Windows.Foundation.Point sidebarHandleStartPoint;
    private double sidebarHandleStartWidth;
    private bool sidebarHandleMoved;
    private NativePoint windowDragStartCursor;
    private Windows.Graphics.PointInt32 windowDragStartPosition;
    private FrameworkElement? draggedSkipMarker;
    private CanvasRenderTarget? videoFrame;
    private CanvasBitmap? mpvFrameBitmap;
    private byte[] mpvFrameBuffer = Array.Empty<byte>();
    private CanvasBitmap? pgsSubtitleBitmap;
    private PgsSubtitleImage? pgsSubtitleImage;
    private int folderTreeGeneration;
    // 将 MediaPlayer 帧服务器输出复制到此画布，使毛玻璃画刷能够采样视频内容。
    private readonly CanvasControl videoCanvas = new() { ClearColor = Colors.Black };
    // 直通呈现模式下视频由系统合成器绘制，这里只叠加自绘的 SUP/PGS 图片字幕。
    private readonly CanvasControl subtitleCanvas = new() { ClearColor = Colors.Transparent };
    private MediaPlayerElement? mediaPresenter;
    private readonly Dictionary<FrameworkElement, DispatcherTimer> sidebarTimers = new();
    // 面板可见性属于实时界面状态，与持久化保存的面板尺寸相互独立。
    private readonly Dictionary<FrameworkElement, DateTime> panelHideDeadlines = new();
    private readonly HashSet<FrameworkElement> visiblePanels = new();
    // 指针当前停留在哪些面板内。悬停期间不启动隐藏计时，只有指针移出后才开始计时；
    // 该状态由面板的进入/离开事件与几何判定共同维护，不依赖指针是否在移动。
    private readonly HashSet<FrameworkElement> hoveredPanels = new();
    private readonly DispatcherTimer panelAutoHideTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer playbackClickTimer = new() { Interval = TimeSpan.FromMilliseconds(260) };
    private readonly DispatcherTimer notificationTimer = new() { Interval = TimeSpan.FromMilliseconds(2500) };
    private readonly SettingsService settingsService = new();
    private readonly PlayerSettings settings;
    // 独立设置窗口（同一时刻只允许一个）；主窗口关闭时要一并关闭，否则进程不会退出。
    private SettingsWindow? settingsWindow;
    private bool settingsWindowDirectPresentBaseline;
    private readonly IReadOnlyList<string> startupArguments;
    private bool startupPlaybackRestored;
    private Windows.Graphics.PointInt32 normalWindowPosition;
    private Windows.Graphics.SizeInt32 normalWindowSize;
    public MainViewModel ViewModel { get; }

    public MainWindow(IReadOnlyList<string>? startupArguments = null)
    {
        this.startupArguments = startupArguments ?? Array.Empty<string>();
        settings = settingsService.Load();
        ViewModel = new MainViewModel(PickMediaFileAsync, ToggleFullScreen, EnterFullScreen,
            ToggleFoldersPanel, TogglePlaylistPanel, DispatcherQueue,
            settings, ShowSettingsAsync, () => settingsService.Save(settings));
        InitializeComponent();
        // 应用界面主题（浅色/深色/跟随系统），须在界面构建后、首次布局前设置。
        ApplyThemeMode();
        // 指针显示/隐藏统一由 CursorManager 管理（透明光标 + Win32 兜底）。
        cursorManager = new CursorManager(() => WinRT.Interop.WindowNative.GetWindowHandle(this));
        ViewModel.Folders.CollectionChanged += Folders_CollectionChanged;
        _ = RebuildFolderTreeAsync();
        // 图标按模式区分：隐私模式使用 player.b.ico，任务栏可据此辨别当前窗口属于哪种模式。
        string iconPath = System.IO.Path.Combine(
        AppContext.BaseDirectory,
        "Images",
        AppMode.IconFileName);

        if (System.IO.File.Exists(iconPath))
            AppWindow.SetIcon(iconPath);

        InitializeKeyboardShortcuts();
        ApplySettings();
        if (FindKeepControlBarToggle() is ToggleButton controlBarToggle)
            controlBarToggle.IsChecked = settings.KeepControlBarVisible;
        InitializeSidebarAnimations();
        AttachPanelHoverTracking();
        PositionSlider.Loaded += PositionSlider_Loaded;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ViewModel.NotificationRequested += ViewModel_NotificationRequested;
        notificationTimer.Tick += NotificationTimer_Tick;
        Root.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(Root_PointerMoved), true);
        Root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(Root_WindowDragPointerPressed), true);
        Root.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(Root_WindowDragPointerReleased), true);
        Root.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(Root_PointerWheelChanged), true);
        Root.PointerCaptureLost += Root_PointerCaptureLost;
        Root.AddHandler(UIElement.RightTappedEvent, new RightTappedEventHandler(Root_RightTapped), true);
        Root.PointerExited += Root_PointerExited;
        panelAutoHideTimer.Tick += PanelAutoHideTimer_Tick;
        panelAutoHideTimer.Start();
        playbackClickTimer.Tick += PlaybackClickTimer_Tick;
        PositionSlider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(PositionSlider_PointerPressed), true);
        PositionSlider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(PositionSlider_PointerReleased), true);
        PositionSlider.ValueChanged += PositionSlider_ValueChanged;
        PositionSlider.SizeChanged += (_, _) => UpdateSkipMarkers();
        Root.SizeChanged += Root_SizeChanged;
        videoCanvas.Draw += VideoCanvas_Draw;
        subtitleCanvas.Draw += SubtitleCanvas_Draw;
        mediaPresenter = Root.FindName("PlayerElement") as MediaPlayerElement;
        // 视频画布放在最底部，字幕叠加画布紧随其后，其余 XAML 面板自然位于两者上方。
        Root.Children.Insert(0, videoCanvas);
        Root.Children.Insert(Math.Min(2, Root.Children.Count), subtitleCanvas);
        ApplyVideoRenderMode();
        ViewModel.MediaPlayer.VideoFrameAvailable += MediaPlayer_VideoFrameAvailable;
        ViewModel.MediaPlayer.SubtitleFrameChanged += MediaPlayer_SubtitleFrameChanged;
        ViewModel.MediaPlayer.MediaOpened += MediaPlayer_MediaOpenedDiagnostics;
        ViewModel.PgsSubtitleFrameChanged += ViewModel_PgsSubtitleFrameChanged;
        ViewModel.MpvFrameAvailable += ViewModel_MpvFrameAvailable;
        RestoreWindowPlacement();
        // 恢复“窗口总在最前”选项并同步开关状态。
        keepOnTopRequested = settings.AlwaysOnTop;
        if (Root.FindName("AlwaysOnTopToggle") is ToggleButton alwaysOnTopToggle)
            alwaysOnTopToggle.IsChecked = keepOnTopRequested;
        ApplyAlwaysOnTopState();
        AppWindow.Changed += AppWindow_Changed;
        ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        // 未调用 SetTitleBar 时，顶部系统标题栏条带默认是窗口拖动区（非客户区输入），
        // TopBar 位于该条带内的按钮上半部分会被拖动输入吞掉，导致"有的点击没效果"。
        // 注册 TopBar 为标题栏后，WinUI 会把其中的按钮标记为可交互区域。
        SetTitleBar(TopBar);
        Activated += MainWindow_Activated;
        Closed += (_, _) =>
        {
            // 关闭过程中抛出的异常会逃逸到 XAML 关闭路径，变成 STOWED_EXCEPTION
            // （0xc000027b）直接崩掉进程，因此这里整体兜底并记录，保证任何一步失败都能正常退出。
            StartExitWatchdog();
            try
            {
                // 设置窗口是独立窗口：必须先关掉它，否则主窗口关闭后进程仍在运行。
                settingsWindow?.Close();
                settingsWindow = null;
                // 若关闭时全屏光标仍处于隐藏状态，先补偿计数并恢复，避免残留隐形光标。
                MakeCursorVisible();
                // 直通模式下 MediaPlayer 仍被 MediaPlayerElement 引用，必须在释放 MediaPlayer 前解绑。
                try
                {
                    mediaPresenter?.SetMediaPlayer(null);
                }
                catch (Exception ex)
                {
                    AppLogService.Warning("ClosedDetachPresenterFailed",
                        "关闭时解绑 MediaPlayerElement 失败", null, ex);
                }
                SaveWindowPlacement(); AppWindow.Changed -= AppWindow_Changed;
                Activated -= MainWindow_Activated;
                ViewModel.MediaPlayer.VideoFrameAvailable -= MediaPlayer_VideoFrameAvailable;
                ViewModel.MediaPlayer.SubtitleFrameChanged -= MediaPlayer_SubtitleFrameChanged;
                ViewModel.MediaPlayer.MediaOpened -= MediaPlayer_MediaOpenedDiagnostics;
                ViewModel.PgsSubtitleFrameChanged -= ViewModel_PgsSubtitleFrameChanged;
                ViewModel.MpvFrameAvailable -= ViewModel_MpvFrameAvailable;
                ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
                ViewModel.NotificationRequested -= ViewModel_NotificationRequested;
                notificationTimer.Stop();
                notificationTimer.Tick -= NotificationTimer_Tick;
                ViewModel.Folders.CollectionChanged -= Folders_CollectionChanged;
                panelAutoHideTimer.Stop();
                playbackClickTimer.Stop();
                playbackClickTimer.Tick -= PlaybackClickTimer_Tick;
                Root.PointerExited -= Root_PointerExited;
                Root.PointerCaptureLost -= Root_PointerCaptureLost;
                Root.RemoveHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(Root_PointerWheelChanged));
                videoFrame?.Dispose();
                pgsSubtitleBitmap?.Dispose();
                mpvFrameBitmap?.Dispose();
                videoCanvas.Draw -= VideoCanvas_Draw;
                subtitleCanvas.Draw -= SubtitleCanvas_Draw;
                PositionSlider.ValueChanged -= PositionSlider_ValueChanged;
                videoCanvas.RemoveFromVisualTree();
                subtitleCanvas.RemoveFromVisualTree();
                ViewModel.Dispose();
            }
            catch (Exception ex)
            {
                AppLogService.Error("WindowClosedCleanupFailed", "窗口关闭清理失败", null, ex);
            }
        };
    }

    /// <summary>
    /// 退出程序。先解绑直通模式下的 MediaPlayerElement 并停止 libmpv 引擎，
    /// 再关闭窗口；任何一步失败都回退到直接结束消息循环，并由看门狗保证进程一定退出。
    /// </summary>
    private void ExitApplication()
    {
        StartExitWatchdog();

        try
        {
            mediaPresenter?.SetMediaPlayer(null);
        }
        catch (Exception ex)
        {
            AppLogService.Warning("ExitDetachPresenterFailed", "退出前解绑 MediaPlayerElement 失败", null, ex);
        }

        try
        {
            ViewModel.StopMpvPlayback();
        }
        catch (Exception ex)
        {
            AppLogService.Warning("ExitStopMpvEngineFailed", "退出前停止 libmpv 引擎失败", null, ex);
        }

        try
        {
            Close();
        }
        catch (Exception ex)
        {
            AppLogService.Error("ExitCloseFailed", "关闭窗口失败，改为直接结束消息循环", null, ex);
        }

        try
        {
            Application.Current.Exit();
        }
        catch (Exception ex)
        {
            AppLogService.Error("ExitApplicationFailed", "结束应用失败", null, ex);
        }
    }

    /// <summary>
    /// 退出看门狗：若正常关闭在 3 秒内没有结束进程（例如解码器/渲染线程卡住），直接结束进程，
    /// 避免出现“关不掉程序”的情况。
    /// </summary>
    private static void StartExitWatchdog()
    {
        if (Interlocked.Exchange(ref exitWatchdogStarted, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(5000).ConfigureAwait(false);
            AppLogService.Warning("ExitWatchdog", "正常关闭未在 5 秒内完成，强制结束进程");
            Environment.Exit(0);
        });
    }

    private static int exitWatchdogStarted;

    private void PositionSlider_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Slider slider)
            return;

        Thumb? thumb = FindVisualChild<Thumb>(slider);
        if (thumb is null)
            return;

        // 鼠标拖动区域
        thumb.Width = 13;
        thumb.Height = 13;

        thumb.ApplyTemplate();

        // 查找实际显示的中心圆点
        FrameworkElement? center = FindVisualChild<Ellipse>(thumb);
        center ??= FindVisualChild<Border>(thumb);

        if (center is null)
            return;

        const double centerSize = 8;

        center.Width = centerSize;
        center.Height = centerSize;
        center.HorizontalAlignment = HorizontalAlignment.Center;
        center.VerticalAlignment = VerticalAlignment.Center;

        if (center is Border border)
            border.CornerRadius = new CornerRadius(centerSize / 2);
    }
    private static T? FindVisualChild<T>(DependencyObject parent)
    where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);

        for (int index = 0; index < count; index++)
        {
            DependencyObject child =
                VisualTreeHelper.GetChild(parent, index);

            if (child is T target)
                return target;

            T? result = FindVisualChild<T>(child);

            if (result is not null)
                return result;
        }

        return null;
    }

    private void RestoreWindowPlacement()
    {
        int width = Math.Max(640, settings.WindowWidth);
        int height = Math.Max(400, settings.WindowHeight);

        if (!settings.HasWindowPlacement)
        {
            AppWindow.Resize(new Windows.Graphics.SizeInt32(width, height));
        }
        else
        {
            var requested = new Windows.Graphics.RectInt32(settings.WindowX, settings.WindowY, width, height);
            DisplayArea display = DisplayArea.GetFromRect(requested, DisplayAreaFallback.Primary);
            Windows.Graphics.RectInt32 workArea = display.WorkArea;
            width = Math.Clamp(width, 640, workArea.Width);
            height = Math.Clamp(height, 400, workArea.Height);
            int x = Math.Clamp(settings.WindowX, workArea.X, workArea.X + workArea.Width - width);
            int y = Math.Clamp(settings.WindowY, workArea.Y, workArea.Y + workArea.Height - height);
            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, width, height));
        }

        normalWindowPosition = AppWindow.Position;
        normalWindowSize = AppWindow.Size;
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        // 注意：Windows App SDK 2.x 中即使处于全屏，AppWindow.Presenter 类型仍可能是
        // OverlappedPresenter，因此不能据此判断是否退出全屏——全屏状态只由
        // ToggleFullScreen / EnterFullScreen 维护，与原始实现保持一致。
        if (isFullScreen) return;
        if (sender.Presenter is OverlappedPresenter presenter &&
            presenter.State != OverlappedPresenterState.Restored) return;
        normalWindowPosition = sender.Position;
        normalWindowSize = sender.Size;
    }

    private void SaveWindowPlacement()
    {
        if (normalWindowSize.Width < 640 || normalWindowSize.Height < 400) return;
        settings.HasWindowPlacement = true;
        settings.WindowX = normalWindowPosition.X;
        settings.WindowY = normalWindowPosition.Y;
        settings.WindowWidth = normalWindowSize.Width;
        settings.WindowHeight = normalWindowSize.Height;
        settingsService.Save(settings);
    }

    private void InitializeKeyboardShortcuts()
    {
        // handledEventsToo 可确保列表控件持有键盘焦点时，播放器快捷键仍然有效。
        Root.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(Root_KeyDown), true);
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (IsTextInputFocused()) return;
        if (e.Key == VirtualKey.Space)
        {
            ViewModel.PlayPauseCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Enter)
        {
            ToggleFullScreen();
            ViewModel.ShowChrome();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Left)
        {
            ViewModel.BackCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Right)
        {
            ViewModel.ForwardCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Add)
        {
            ViewModel.NextCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Subtract)
        {
            ViewModel.PreviousCommand.Execute(null);
            e.Handled = true;
        }
        else if ((InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down)
        {
            if (e.Key >= VirtualKey.NumberPad0 && e.Key <= VirtualKey.NumberPad4)
            {
                int number = (int)e.Key - (int)VirtualKey.NumberPad0;
                // 全屏状态下直接改动窗口几何，会让 isFullScreen 与实际呈现状态不一致：
                // 之后按 Enter 走的是"退出全屏"分支，看起来就像 Enter 失效了。先退回窗口化。
                if (isFullScreen) ToggleFullScreen();
                ViewModel.ChangefromSiseze(this, (WindowPosition)number);
                e.Handled = true;
            }
        }
    }

    private static bool IsTextInputFocused()
    {
        object? focused = FocusManager.GetFocusedElement();
        return focused is TextBox or NumberBox or PasswordBox or RichEditBox;
    }

    private void Root_WindowDragPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        NotifyCursorActivity();
        if (IsSidebarHandleSource(e.OriginalSource as DependencyObject)) return;
        if (!e.GetCurrentPoint(Root).Properties.IsLeftButtonPressed) return;
        Windows.Foundation.Point point = e.GetCurrentPoint(Root).Position;
        if (point.Y >= Root.ActualHeight - 112) return;
        if (LeftSidebar.IsHitTestVisible && point.Y >= 52 && point.X <= LeftSidebar.Width) return;
        if (RightSidebar.IsHitTestVisible && point.Y >= 52 && point.X >= Root.ActualWidth - RightSidebar.Width) return;
        if (IsInteractiveElement(e.OriginalSource as DependencyObject)) return;
        if (!GetCursorPos(out windowDragStartCursor)) return;

        windowDragStartPosition = AppWindow.Position;
        windowDragMoved = false;
        windowDragStartedInPlaybackArea = point.Y >= 52;
        isDraggingWindow = Root.CapturePointer(e.Pointer);
        e.Handled = isDraggingWindow;
    }

    private void Root_WindowDragPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        NotifyCursorActivity();
        if (!isDraggingWindow) return;
        isDraggingWindow = false;
        Root.ReleasePointerCapture(e.Pointer);
        if (!windowDragMoved && windowDragStartedInPlaybackArea)
            RegisterPlaybackAreaClick();
        e.Handled = true;
    }

    private void Root_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => isDraggingWindow = false;

    private void Root_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        NotifyCursorActivity();
        Windows.Foundation.Point point = e.GetCurrentPoint(Root).Position;
        if (point.Y < 52 || point.Y >= Root.ActualHeight - 112) return;
        if (LeftSidebar.IsHitTestVisible && point.X <= LeftSidebar.Width) return;
        if (RightSidebar.IsHitTestVisible && point.X >= Root.ActualWidth - RightSidebar.Width) return;
        if (IsInteractiveElement(e.OriginalSource as DependencyObject)) return;

        int delta = e.GetCurrentPoint(Root).Properties.MouseWheelDelta;
        if (delta == 0) return;
        ViewModel.AdjustVolume(delta > 0 ? 5 : -5);
        e.Handled = true;
    }

    private void RegisterPlaybackAreaClick()
    {
        if (playbackClickTimer.IsEnabled)
        {
            playbackClickTimer.Stop();
            ToggleFullScreen();
            ViewModel.ShowChrome();
            return;
        }
        playbackClickTimer.Start();
    }

    private void PlaybackClickTimer_Tick(object? sender, object e)
    {
        playbackClickTimer.Stop();
        // 计时器还承担双击检测：即使关闭了单击播放/暂停，也要等它超时以排除双击。
        if (settings.ClickToPlayPause)
            ViewModel.PlayPauseCommand.Execute(null);
    }

    private static bool IsInteractiveElement(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is ButtonBase or Slider or ListViewBase or TextBox or NumberBox)
                return true;
            element = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element);
        }
        return false;
    }

    private bool IsSidebarHandleSource(DependencyObject? element)
    {
        while (element is not null)
        {
            if (ReferenceEquals(element, LeftSidebarHandle) || ReferenceEquals(element, RightSidebarHandle))
                return true;
            element = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element);
        }
        return false;
    }

    private async void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        // 全屏自动隐藏光标只在窗口处于激活状态时进行（见 EnforceCursorAutoHide）。
        windowActive = args.WindowActivationState != WindowActivationState.Deactivated;
        // 失去激活时指针的进入/离开事件可能收不到，清空悬停状态避免面板被永久“锁”在悬停里。
        if (!windowActive) hoveredPanels.Clear();
        // 窗口失焦（Alt+Tab 等）时立即恢复指针，避免把系统箭头“留空”给其他程序。
        if (!windowActive && fullScreenCursorHidden)
            RestoreCursorAfterFullScreen();
        if (startupPlaybackRestored) return;
        startupPlaybackRestored = true;
        if (startupArguments.Count > 0)
            await ViewModel.LoadStartupArgumentsAsync(startupArguments);
        else
            await ViewModel.RestoreLastPlaybackAsync();
    }

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.AcceptedOperation = DataPackageOperation.Link;
        e.DragUIOverride.Caption = "添加到播放列表";
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsContentVisible = true;
    }

    private void SubtitleMenuFlyout_Opening(object sender, object e)
    {
        if (sender is not MenuFlyout menu) return;
        menu.Items.Clear();

        // 本项目字幕总开关：关闭后应用不再绘制自己的字幕层，
        // 适合杜比视界引擎模式（字幕由 libmpv 渲染）下避免两行字幕重合。
        var ownSubtitles = new ToggleMenuFlyoutItem
        {
            Text = "本项目字幕",
            IsChecked = !settings.HideOwnSubtitles
        };
        ownSubtitles.Click += (_, _) =>
        {
            settings.HideOwnSubtitles = !ownSubtitles.IsChecked;
            settingsService.Save(settings);
            ViewModel.ApplyOwnSubtitleSetting();
            ViewModel.ShowNotification(settings.HideOwnSubtitles
                ? "已关闭本项目字幕（字幕由播放引擎负责）"
                : "已开启本项目字幕");
        };
        menu.Items.Add(ownSubtitles);
        menu.Items.Add(new MenuFlyoutSeparator());

        var disabled = new ToggleMenuFlyoutItem
        {
            Text = "关闭字幕",
            IsChecked = ViewModel.AreSubtitlesOff()
        };
        disabled.Click += (_, _) => ViewModel.SelectSubtitleTrack(-1);
        menu.Items.Add(disabled);

        IReadOnlyList<MediaTrackOption> tracks = ViewModel.GetSubtitleTrackOptions();
        foreach (MediaTrackOption track in tracks)
        {
            var item = new ToggleMenuFlyoutItem { Text = track.DisplayName, IsChecked = track.IsSelected };
            int index = track.Index;
            item.Click += (_, _) => ViewModel.SelectSubtitleTrack(index);
            menu.Items.Add(item);
        }
        if (tracks.Count == 0)
            menu.Items.Add(new MenuFlyoutItem { Text = "未检测到字幕轨道", IsEnabled = false });

        menu.Items.Add(new MenuFlyoutSeparator());
        var loadExternal = new MenuFlyoutItem
        {
            Text = "加载外挂字幕…",
            Icon = new FontIcon { Glyph = "\uE8E5" }
        };
        loadExternal.Click += async (_, _) => await PickExternalSubtitleAsync();
        menu.Items.Add(loadExternal);
    }

    private async Task PickExternalSubtitleAsync()
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.VideosLibrary,
            ViewMode = PickerViewMode.List
        };
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            WinRT.Interop.WindowNative.GetWindowHandle(this));
        // 与字幕加载时的扩展名校验共用同一份列表（Models\SupportedMedia.cs）。
        foreach (string extension in SupportedMedia.SubtitleExtensions)
            picker.FileTypeFilter.Add(extension);

        StorageFile? file = await picker.PickSingleFileAsync();
        if (file is not null) await ViewModel.LoadExternalSubtitleAsync(file);
    }

    private void AudioMenuFlyout_Opening(object sender, object e)
    {
        if (sender is not MenuFlyout menu) return;
        menu.Items.Clear();
        IReadOnlyList<MediaTrackOption> tracks = ViewModel.GetAudioTrackOptions();
        foreach (MediaTrackOption track in tracks)
        {
            var item = new ToggleMenuFlyoutItem { Text = track.DisplayName, IsChecked = track.IsSelected };
            int index = track.Index;
            item.Click += (_, _) => ViewModel.SelectAudioTrack(index);
            menu.Items.Add(item);
        }
        if (tracks.Count == 0)
            menu.Items.Add(new MenuFlyoutItem { Text = "未检测到音轨", IsEnabled = false });
    }

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        IReadOnlyList<IStorageItem> items = await e.DataView.GetStorageItemsAsync();
        await ViewModel.AddDroppedItemsAsync(items);
    }

    private void Folders_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        _ = RebuildFolderTreeAsync();

    private async Task RebuildFolderTreeAsync()
    {
        int generation = Interlocked.Increment(ref folderTreeGeneration);
        FolderTree.RootNodes.Clear();
        IReadOnlyList<FolderTreeItem> roots = await ViewModel.GetFolderTreeItemsAsync(
            ViewModel.Folders.ToArray());
        if (generation != folderTreeGeneration) return;

        FolderTree.RootNodes.Clear();
        foreach (FolderTreeItem folder in roots)
        {
            FolderTree.RootNodes.Add(new TreeViewNode
            {
                Content = folder,
                HasUnrealizedChildren = folder.HasSubfolders
            });
        }
    }

    private async void FolderTree_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        // 按需加载一层目录，避免一次性遍历大型目录或网络文件夹树。
        if (args.Node.Content is not FolderTreeItem folder ||
            folder.ChildrenLoaded || folder.ChildrenLoading) return;
        using IDisposable loading = ViewModel.BeginLoading("正在加载子文件夹…");
        folder.ChildrenLoading = true;
        try
        {
            IReadOnlyList<FolderTreeItem> children = await ViewModel.GetSubfoldersAsync(folder.Path);
            args.Node.Children.Clear();
            for (int index = 0; index < children.Count; index++)
            {
                FolderTreeItem child = children[index];
                args.Node.Children.Add(new TreeViewNode
                {
                    Content = child,
                    HasUnrealizedChildren = child.HasSubfolders
                });
                // 大型目录每批让出一次 UI 线程，保证左右侧栏动画和输入持续响应。
                if ((index + 1) % 32 == 0) await Task.Yield();
            }
            folder.ChildrenLoaded = true;
            args.Node.HasUnrealizedChildren = false;

            // Expanding 是 async void 事件；第一次 await 时控件可能因为尚无实际子节点
            // 而结束本次展开。等当前事件返回消息循环后再恢复展开状态，使首次单击即可展开。
            if (args.Node.Children.Count > 0)
                DispatcherQueue.TryEnqueue(() => args.Node.IsExpanded = true);
        }
        finally
        {
            folder.ChildrenLoading = false;
        }
    }

    private async void FolderTree_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (FolderTree.SelectedNode?.Content is not FolderTreeItem folder) return;
        await ViewModel.LoadFolderAsync(folder.Path);
        e.Handled = true;
    }

    private async void ClearFoldersButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Folders.Count == 0)
        {
            ViewModel.ClearFolders();
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "清空媒体文件夹列表？",
            Content = $"将移除左侧栏中的 {ViewModel.Folders.Count} 个文件夹，不会改变播放列表。",
            PrimaryButtonText = "清空",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            ViewModel.ClearFolders();
    }

    private async void ClearPlaylistButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Playlist.Count == 0)
        {
            ViewModel.ClearPlaylist();
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "清空播放列表？",
            Content = $"将移除右侧栏中的 {ViewModel.Playlist.Count} 个项目，当前媒体会继续播放。",
            PrimaryButtonText = "清空",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            ViewModel.ClearPlaylist();
    }

    private void InitializeSidebarAnimations()
    {
        // 使用位移属性执行滑入滑出动画，同时保持面板布局不发生重新排列。
        ElementCompositionPreview.SetIsTranslationEnabled(TopBar, true);
        ElementCompositionPreview.SetIsTranslationEnabled(ControlBar, true);
        ElementCompositionPreview.SetIsTranslationEnabled(LeftSidebar, true);
        ElementCompositionPreview.SetIsTranslationEnabled(RightSidebar, true);
        ElementCompositionPreview.SetIsTranslationEnabled(LeftSidebarHandle, true);
        ElementCompositionPreview.SetIsTranslationEnabled(RightSidebarHandle, true);
        LeftSidebar.Translation = new Vector3(-(float)LeftSidebar.Width, 0, 0);
        RightSidebar.Translation = new Vector3((float)RightSidebar.Width, 0, 0);
        LeftSidebarHandle.Translation = LeftSidebar.Translation;
        RightSidebarHandle.Translation = RightSidebar.Translation;
        TopBar.Translation = new Vector3(0, -52, 0);
        ControlBar.Translation = new Vector3(0, 112, 0);
        TopBar.IsHitTestVisible = false;
        ControlBar.IsHitTestVisible = false;
        LeftSidebar.IsHitTestVisible = false;
        RightSidebar.IsHitTestVisible = false;
        LeftSidebarHandle.IsHitTestVisible = false;
        RightSidebarHandle.IsHitTestVisible = false;
        if (settings.KeepControlBarVisible)
        {
            ControlBar.Translation = Vector3.Zero;
            ControlBar.IsHitTestVisible = true;
            visiblePanels.Add(ControlBar);
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.LeftSidebarWidth))
            SetPanelVisible(LeftSidebar, ViewModel.LeftSidebarWidth > 0);
        else if (e.PropertyName == nameof(MainViewModel.RightSidebarWidth))
            SetPanelVisible(RightSidebar, ViewModel.RightSidebarWidth > 0);
        else if (e.PropertyName == nameof(MainViewModel.Duration) ||
                 e.PropertyName == nameof(MainViewModel.IntroMarkerText) ||
                 e.PropertyName == nameof(MainViewModel.OutroMarkerText))
            UpdateSkipMarkers();
        else if (e.PropertyName == nameof(MainViewModel.IsPlaying))
            ApplyPlaybackActivity(ViewModel.IsPlaying);
        else if (e.PropertyName == nameof(MainViewModel.IsMediaOpen) && !ViewModel.IsMediaOpen)
            videoCanvas.Invalidate();
    }

    private void ViewModel_NotificationRequested(object? sender, UserNotificationEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            OperationInfoBar.Title = e.IsError ? "操作失败" : "操作成功";
            OperationInfoBar.Message = e.Message;
            OperationInfoBar.Severity = e.IsError ? InfoBarSeverity.Error : InfoBarSeverity.Success;
            Windows.UI.Color messageColor = e.IsError
                ? Windows.UI.Color.FromArgb(255, 255, 96, 96)
                : Windows.UI.Color.FromArgb(255, 88, 220, 142);
            if (OperationInfoBar.Resources["InfoBarTitleForeground"] is Microsoft.UI.Xaml.Media.SolidColorBrush titleBrush)
                titleBrush.Color = messageColor;
            if (OperationInfoBar.Resources["InfoBarMessageForeground"] is Microsoft.UI.Xaml.Media.SolidColorBrush messageBrush)
                messageBrush.Color = messageColor;
            OperationInfoBarHost.Visibility = Visibility.Visible;
            OperationInfoBar.IsOpen = true;
            notificationTimer.Stop();
            notificationTimer.Start();
        });
    }

    private void NotificationTimer_Tick(object? sender, object e)
    {
        notificationTimer.Stop();
        OperationInfoBar.IsOpen = false;
        OperationInfoBarHost.Visibility = Visibility.Collapsed;
    }

    private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e) =>
        ViewModel.ShowVolumeNotification(e.NewValue);

    private void UpdateSkipMarkers()
    {
        double duration = ViewModel.Duration;
        double width = PositionSlider.ActualWidth;
        if (duration <= 1 || width <= 0) return;

        double intro = settings.SkipIntroSeconds;
        IntroMarker.Visibility = Visibility.Visible;
        IntroMarker.Margin = new Thickness(12 + Math.Clamp(intro / duration, 0, 1) * width - IntroMarker.Width / 2, -25, 0, 0);

        double outroStart = duration - settings.SkipOutroSeconds;
        OutroMarker.Visibility = Visibility.Visible;
        OutroMarker.Margin = new Thickness(12 + Math.Clamp(outroStart / duration, 0, 1) * width - OutroMarker.Width / 2, -25, 0, 0);
    }

    private void SetPanelVisible(FrameworkElement panel, bool show)
    {
        // visiblePanels 是标题按钮、悬停逻辑和调整块判断面板状态的唯一依据。
        if (panel == ControlBar && settings.KeepControlBarVisible && !show) return;
        if (show) visiblePanels.Add(panel); else visiblePanels.Remove(panel);
        if (show) panelHideDeadlines.Remove(panel);
        // 隐藏后的面板不再算“悬停”，避免指针不再产生离开事件时留下过期的悬停状态。
        if (!show) hoveredPanels.Remove(panel);
        FrameworkElement? handle = GetSidebarHandle(panel);
        if (handle is not null)
        {
            UpdateSidebarHandlePositions();
            if (show) handle.Visibility = Visibility.Visible;
            handle.IsHitTestVisible = show;
        }
        AnimatePanel(panel, show, GetHiddenTranslation(panel), handle);
    }

    /// <summary>
    /// 让悬停状态由面板自身的进入/离开事件驱动：
    /// 指针停在面板上（即使完全不动）就不会被隐藏计时命中，指针移开的那一刻才开始计时。
    /// 侧栏调整块紧贴侧栏边缘，指针从侧栏移到调整块上不应被判定为“已离开”。
    /// </summary>
    private void AttachPanelHoverTracking()
    {
        foreach (FrameworkElement panel in
                 new FrameworkElement[] { TopBar, ControlBar, LeftSidebar, RightSidebar })
        {
            FrameworkElement tracked = panel;
            tracked.PointerEntered += (_, _) => UpdatePanelHover(tracked, true);
            tracked.PointerExited += (_, _) => UpdatePanelHover(tracked, false);
        }

        foreach ((FrameworkElement handle, FrameworkElement panel) in
                 new (FrameworkElement, FrameworkElement)[]
                 {
                     (LeftSidebarHandle, LeftSidebar),
                     (RightSidebarHandle, RightSidebar)
                 })
        {
            FrameworkElement owner = panel;
            handle.PointerEntered += (_, _) => UpdatePanelHover(owner, true);
            handle.PointerExited += (_, _) => UpdatePanelHover(owner, false);
        }
    }

    private void ToggleFoldersPanel()
    {
        panelHideDeadlines.Remove(LeftSidebar);
        SetPanelVisible(LeftSidebar, !visiblePanels.Contains(LeftSidebar));
    }

    private void TogglePlaylistPanel()
    {
        panelHideDeadlines.Remove(RightSidebar);
        SetPanelVisible(RightSidebar, !visiblePanels.Contains(RightSidebar));
    }

    private Vector3 GetHiddenTranslation(FrameworkElement panel)
    {
        if (panel == TopBar) return new Vector3(0, -52, 0);
        if (panel == ControlBar) return new Vector3(0, 112, 0);
        if (panel == LeftSidebar) return new Vector3(-(float)LeftSidebar.Width, 0, 0);
        return new Vector3((float)RightSidebar.Width, 0, 0);
    }

    private void UpdatePanelHover(FrameworkElement panel, bool pointerInside)
    {
        // 记录悬停状态：悬停期间永远不累计时；由悬停变为非悬停的那一次才是“移出”。
        bool wasHovered = hoveredPanels.Remove(panel);
        if (pointerInside) hoveredPanels.Add(panel);

        if (panel == ControlBar && settings.KeepControlBarVisible)
        {
            panelHideDeadlines.Remove(panel);
            if (!visiblePanels.Contains(panel)) SetPanelVisible(panel, true);
            return;
        }
        if (pointerInside)
        {
            panelHideDeadlines.Remove(panel);
            if (!visiblePanels.Contains(panel)) SetPanelVisible(panel, true);
        }
        else if (visiblePanels.Contains(panel) &&
                 (wasHovered || !panelHideDeadlines.ContainsKey(panel)))
        {
            // 指针移出面板时才开始隐藏计时（每次“移出”都从当前时刻重新计）；
            // 指针在面板外持续移动不会延长计时，因此面板仍会按设定延迟隐藏。
            panelHideDeadlines[panel] = DateTime.UtcNow.AddSeconds(settings.AutoHideDelay);
        }
    }

    private void ApplyControlBarVisibilitySetting()
    {
        panelHideDeadlines.Remove(ControlBar);
        if (settings.KeepControlBarVisible)
        {
            SetPanelVisible(ControlBar, true);
        }
        else if (visiblePanels.Contains(ControlBar))
        {
            panelHideDeadlines[ControlBar] =
                DateTime.UtcNow.AddSeconds(settings.AutoHideDelay);
        }
    }

    private void KeepControlBarToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton toggleButton) return;

        bool keepVisible = toggleButton.IsChecked == true;
        if (settings.KeepControlBarVisible == keepVisible) return;

        settings.KeepControlBarVisible = keepVisible;
        settingsService.Save(settings);

        if (keepVisible)
        {
            ApplyControlBarVisibilitySetting();
        }
        else
        {
            // 点击发生在底栏内部，先保持显示；鼠标移出后再进入原有的延时隐藏流程。
            panelHideDeadlines.Remove(ControlBar);
            if (!visiblePanels.Contains(ControlBar)) SetPanelVisible(ControlBar, true);
        }

        ViewModel.ShowNotification(keepVisible ? "底部控制栏已设为固定显示" : "底部控制栏已设为自动隐藏");
    }

    private ToggleButton? FindKeepControlBarToggle() =>
        Root.FindName("KeepControlBarToggle") as ToggleButton;

    private void PanelAutoHideTimer_Tick(object? sender, object e)
    {
        DateTime now = DateTime.UtcNow;
        EnforceCursorAutoHide(now);
        // 隐藏期间周期性压制，防止输入栈把指针弹回。
        if (fullScreenCursorHidden) cursorManager.KeepHidden();
        foreach (FrameworkElement panel in panelHideDeadlines
                     .Where(item => item.Value <= now).Select(item => item.Key).ToArray())
        {
            panelHideDeadlines.Remove(panel);
            SetPanelVisible(panel, false);
        }
    }

    private FrameworkElement? GetSidebarHandle(FrameworkElement panel) =>
        panel == LeftSidebar ? LeftSidebarHandle :
        panel == RightSidebar ? RightSidebarHandle : null;

    private void AnimatePanel(FrameworkElement panel, bool show, Vector3 hiddenTranslation,
        FrameworkElement? companion = null)
    {
        panel.IsHitTestVisible = show;
        if (sidebarTimers.Remove(panel, out DispatcherTimer? previous)) previous.Stop();

        Vector3 start = panel.Translation;
        Vector3 target = show ? Vector3.Zero : hiddenTranslation;
        if (Vector3.Distance(start, target) < 0.5f)
        {
            panel.Translation = target;
            if (companion is not null)
            {
                companion.Translation = target;
                if (!show) companion.Visibility = Visibility.Collapsed;
            }
            return;
        }

        DateTime started = DateTime.UtcNow;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
        timer.Tick += (_, _) =>
        {
            double progress = Math.Min(1d, (DateTime.UtcNow - started).TotalMilliseconds /
                Math.Max(100d, settings.AnimationDuration));
            double eased = progress * progress * (3d - 2d * progress);
            panel.Translation = Vector3.Lerp(start, target, (float)eased);
            if (companion is not null) companion.Translation = panel.Translation;
            if (progress < 1d) return;
            timer.Stop();
            sidebarTimers.Remove(panel);
            panel.Translation = target;
            if (companion is not null)
            {
                companion.Translation = target;
                if (!show) companion.Visibility = Visibility.Collapsed;
            }
        };
        sidebarTimers[panel] = timer;
        timer.Start();
    }

    private void MediaPlayer_VideoFrameAvailable(MediaPlayer sender, object args)
    {
        DispatcherQueue.TryEnqueue(() => videoCanvas.Invalidate());
    }

    private void MediaPlayer_SubtitleFrameChanged(MediaPlayer sender, object args)
    {
        // 暂停状态下视频帧不会持续刷新，字幕帧变化时仍需主动重绘画布。
        DispatcherQueue.TryEnqueue(() => videoCanvas.Invalidate());
    }

    /// <summary>
    /// 播放状态变化：仅在"正在播放"时允许全屏自动隐藏指针；一旦暂停/停止立即恢复指针显示；
    /// 恢复播放后重新开始空闲计时。状态源统一取 ViewModel.IsPlaying，这样系统媒体框架与
    /// libmpv 杜比引擎两种播放路径都能正确驱动自动隐藏。
    /// </summary>
    private void ApplyPlaybackActivity(bool playing)
    {
        if (playing == isPlaybackActive) return;
        isPlaybackActive = playing;
        if (playing)
        {
            // 恢复播放：重新计时，指针静止超过延时后再次自动隐藏。
            if (isFullScreen)
                cursorHideDeadline = DateTime.UtcNow.AddSeconds(settings.AutoHideDelay);
        }
        else if (fullScreenCursorHidden)
        {
            // 暂停/停止：立即恢复指针显示并取消隐藏计划。
            cursorHideDeadline = DateTime.MaxValue;
            MakeCursorVisible();
        }
    }

    private void ViewModel_PgsSubtitleFrameChanged(PgsSubtitleImage? image)
    {
        pgsSubtitleImage = image;
        pgsSubtitleBitmap?.Dispose();
        pgsSubtitleBitmap = null;
        // 自绘模式在同画布绘制字幕；直通模式由叠加画布绘制，两者都需要重绘。
        videoCanvas.Invalidate();
        subtitleCanvas.Invalidate();
    }

    /// <summary>
    /// libmpv 引擎渲染出新帧（渲染线程触发）：切回 UI 线程上传到 Win2D 位图并重绘画布。
    /// </summary>
    private void ViewModel_MpvFrameAvailable() => DispatcherQueue.TryEnqueue(() =>
    {
        try
        {
            UpdateMpvFrameBitmap();
        }
        catch (Exception ex)
        {
            AppLogService.Warning("MpvFrameUploadFailed", "上传 libmpv 帧到画布失败", null, ex);
        }
        finally
        {
            ViewModel.MarkMpvFrameConsumed();
        }
        videoCanvas.Invalidate();
    });

    private bool mpvPresentFailedLogged;

    private void UpdateMpvFrameBitmap()
    {
        MpvDvEngine? engine = ViewModel.MpvEngine;
        if (engine is null) return;

        int needed = engine.FrameWidth * engine.FrameHeight * 4;
        if (needed <= 0) return;
        if (mpvFrameBuffer.Length != needed) mpvFrameBuffer = new byte[needed];
        if (!engine.CopyFrame(mpvFrameBuffer, out int width, out int height, out int stride)) return;
        if (stride != width * 4) return;

        if (mpvFrameBitmap is not null &&
            (mpvFrameBitmap.SizeInPixels.Width != width || mpvFrameBitmap.SizeInPixels.Height != height))
        {
            mpvFrameBitmap.Dispose();
            mpvFrameBitmap = null;
        }

        if (mpvFrameBitmap is null)
        {
            // 引擎输出 BGRA（自上而下），与 B8G8R8A8 位图逐字节一致，可直接创建。
            mpvFrameBitmap = CanvasBitmap.CreateFromBytes(videoCanvas, mpvFrameBuffer, width, height,
                DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Ignore);
            return;
        }

        mpvFrameBitmap.SetPixelBytes(mpvFrameBuffer);
    }

    private void VideoCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        // 保持视频宽高比并使用黑边填充，避免拉伸视频内容。
        float width = (float)sender.ActualWidth;
        float height = (float)sender.ActualHeight;
        if (width < 1 || height < 1) return;

        // CanvasControl 会保留上一次绘制结果。媒体源被关闭后必须先清屏，
        // 否则空目录或停止播放时仍可能看到上一帧画面。
        args.DrawingSession.Clear(Colors.Black);

        // libmpv 引擎（杜比视界内容）直接绘制引擎输出的位图。
        if (ViewModel.IsMpvEngineActive && mpvFrameBitmap is not null)
        {
            Windows.Foundation.Size bitmapSize = mpvFrameBitmap.Size;
            float fit = Math.Min(width / (float)bitmapSize.Width, height / (float)bitmapSize.Height);
            float fitWidth = (float)bitmapSize.Width * fit;
            float fitHeight = (float)bitmapSize.Height * fit;
            try
            {
                args.DrawingSession.DrawImage(mpvFrameBitmap,
                    new Windows.Foundation.Rect((width - fitWidth) / 2, (height - fitHeight) / 2, fitWidth, fitHeight),
                    mpvFrameBitmap.Bounds);
            }
            catch (Exception ex) when (!mpvPresentFailedLogged)
            {
                // 只在首次失败时记录，避免每帧刷日志。
                mpvPresentFailedLogged = true;
                AppLogService.Error("MpvFramePresentFailed", "绘制 libmpv 帧失败", null, ex);
            }
            return;
        }

        if (videoFrame is null ||
            Math.Abs(videoFrame.Size.Width - width) > 1 ||
            Math.Abs(videoFrame.Size.Height - height) > 1)
        {
            videoFrame?.Dispose();
            videoFrame = new CanvasRenderTarget(sender, width, height);
        }

        uint sourceWidth = ViewModel.MediaPlayer.PlaybackSession.NaturalVideoWidth;
        uint sourceHeight = ViewModel.MediaPlayer.PlaybackSession.NaturalVideoHeight;
        if (sourceWidth == 0 || sourceHeight == 0) return;

        float targetWidth = videoFrame.SizeInPixels.Width;
        float targetHeight = videoFrame.SizeInPixels.Height;
        float scale = Math.Min(targetWidth / sourceWidth, targetHeight / sourceHeight);
        float drawWidth = sourceWidth * scale;
        float drawHeight = sourceHeight * scale;
        float left = (targetWidth - drawWidth) / 2;
        float top = (targetHeight - drawHeight) / 2;

        using (CanvasDrawingSession clearSession = videoFrame.CreateDrawingSession())
            clearSession.Clear(Colors.Black);

        try
        {
            ViewModel.MediaPlayer.CopyFrameToVideoSurface(videoFrame,
                new Windows.Foundation.Rect(left, top, drawWidth, drawHeight));
            // 帧服务器模式不会自动呈现 SUP/PGS、VobSub 等图片字幕，
            // 将系统生成的字幕帧直接合成到同一视频表面。
            ViewModel.MediaPlayer.RenderSubtitlesToSurface(videoFrame,
                new Windows.Foundation.Rect(left, top, drawWidth, drawHeight));
        }
        catch (COMException)
        {
            return;
        }

        args.DrawingSession.DrawImage(videoFrame,
            new Windows.Foundation.Rect(0, 0, width, height), videoFrame.Bounds);

        // 帧服务器模式不会自动呈现 SUP/PGS 等图片字幕，因此叠加在同一画布上绘制。
        DrawPgsSubtitle(args.DrawingSession, width, height);
    }

    private void SubtitleCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        // 直通模式下视频由系统呈现，叠加画布保持透明，只绘制自绘的图片字幕。
        args.DrawingSession.Clear(Colors.Transparent);
        DrawPgsSubtitle(args.DrawingSession, (float)sender.ActualWidth, (float)sender.ActualHeight);
    }

    /// <summary>
    /// 绘制自绘的 SUP/PGS 图片字幕。帧服务器模式下与视频帧同画布绘制，
    /// 直通模式下由独立叠加画布绘制，两处共用同一套缩放与定位计算。
    /// </summary>
    private void DrawPgsSubtitle(CanvasDrawingSession session, float canvasWidth, float canvasHeight)
    {
        // 关闭“本项目字幕”或由 libmpv 引擎渲染时，本程序不再自绘图片字幕。
        if (pgsSubtitleImage is null || canvasWidth < 1 || canvasHeight < 1 ||
            settings.HideOwnSubtitles || ViewModel.IsMpvEngineActive) return;

        uint sourceWidth = ViewModel.MediaPlayer.PlaybackSession.NaturalVideoWidth;
        uint sourceHeight = ViewModel.MediaPlayer.PlaybackSession.NaturalVideoHeight;
        if (sourceWidth == 0 || sourceHeight == 0) return;

        pgsSubtitleBitmap ??= CanvasBitmap.CreateFromBytes(session,
            pgsSubtitleImage.Pixels, pgsSubtitleImage.Width, pgsSubtitleImage.Height,
            DirectXPixelFormat.B8G8R8A8UIntNormalized, 96,
            CanvasAlphaMode.Premultiplied);

        // videoFrame.SizeInPixels 使用物理像素，而 CanvasControl 的绘制坐标使用 DIP。
        // 直接复用视频的绘制矩形会在高 DPI 下把底部字幕画到画布外。
        // 因此按最终显示画布重新计算等比缩放后的可见视频区域。
        double displayScale = Math.Min(canvasWidth / sourceWidth, canvasHeight / sourceHeight);
        double displayWidth = sourceWidth * displayScale;
        double displayHeight = sourceHeight * displayScale;
        double displayLeft = (canvasWidth - displayWidth) / 2;
        double displayTop = (canvasHeight - displayHeight) / 2;
        double subtitleLeft = displayLeft +
            pgsSubtitleImage.X / (double)pgsSubtitleImage.CanvasWidth * displayWidth;
        double subtitleTop = displayTop +
            pgsSubtitleImage.Y / (double)pgsSubtitleImage.CanvasHeight * displayHeight;
        double subtitleWidth =
            pgsSubtitleImage.Width / (double)pgsSubtitleImage.CanvasWidth * displayWidth;
        double subtitleHeight =
            pgsSubtitleImage.Height / (double)pgsSubtitleImage.CanvasHeight * displayHeight;
        session.DrawImage(pgsSubtitleBitmap,
            new Windows.Foundation.Rect(subtitleLeft, subtitleTop, subtitleWidth, subtitleHeight),
            pgsSubtitleBitmap.Bounds);
    }

    /// <summary>
    /// 应用“自绘 / 直通”两种视频呈现模式：
    /// 自绘模式由 Win2D 画布呈现帧服务器输出；直通模式把 MediaPlayer 交给
    /// MediaPlayerElement，由系统合成器输出，HDR/杜比内容据此才有机会正确进 HDR。
    /// </summary>
    private void ApplyVideoRenderMode()
    {
        // libmpv 引擎的画面由本应用的 Win2D 画布呈现，因此该模式下画布必须可见，
        // “直通呈现”只影响系统媒体框架的输出路径，不能把画布折叠掉。
        bool mpvMode = settings.UseLibMpvEngine && ViewModel.IsMpvEngineAvailable;
        bool direct = settings.HdrDirectPresent && !mpvMode;
        if (mediaPresenter is not null)
        {
            try
            {
                mediaPresenter.SetMediaPlayer(direct ? ViewModel.MediaPlayer : null);
            }
            catch (Exception ex)
            {
                AppLogService.Warning("RenderModeAttachFailed",
                    "绑定 MediaPlayerElement 失败", new { DirectPresent = direct }, ex);
            }
            mediaPresenter.Visibility = direct ? Visibility.Visible : Visibility.Collapsed;
        }
        videoCanvas.Visibility = direct ? Visibility.Collapsed : Visibility.Visible;
        subtitleCanvas.Visibility = direct ? Visibility.Visible : Visibility.Collapsed;
    }

    private void MediaPlayer_MediaOpenedDiagnostics(MediaPlayer sender, object args) =>
        DispatcherQueue.TryEnqueue(ReportHdrDiagnostics);

    /// <summary>
    /// 汇总一次 HDR/杜比诊断：写入日志、返回可读文本。
    /// 用来回答“画面异常是内容本身还是输出路径的问题”以及“到底有没有进 HDR”。
    /// </summary>
    private string BuildHdrDiagnosticsText()
    {
        string? path = ViewModel.CurrentMediaPath;
        string display = HdrDiagnosticsService.DescribeDisplay(AppWindow.Id);
        string sessionReport = HdrDiagnosticsService.DescribeSession(ViewModel.MediaPlayer);
        string track = HdrDiagnosticsService.DescribeVideoTrack(ViewModel.PlaybackItem);
        AppLogService.Information("HdrDiagnostics", "HDR/杜比视界诊断", new
        {
            MediaPath = AppMode.IsPrivacy ? null : path,
            FileName = AppMode.IsPrivacy ? System.IO.Path.GetFileName(path) : null,
            DirectPresent = settings.HdrDirectPresent,
            LibMpvEngine = settings.UseLibMpvEngine,
            Display = display,
            Session = sessionReport,
            VideoTrack = track
        });
        return string.Join(Environment.NewLine,
            $"媒体：{path ?? "（当前没有打开的媒体）"}",
            sessionReport,
            display,
            track,
            $"日志文件：{AppLogService.LogPath}");
    }

    /// <summary>媒体打开时自动输出一次诊断，并给出简短提示。</summary>
    private void ReportHdrDiagnostics()
    {
        string text = BuildHdrDiagnosticsText();
        string summary = string.Join("；", text.Split(Environment.NewLine).Skip(1).Take(2));
        ViewModel.ShowNotification(summary);
        _ = ProbeDolbyVisionAsync(ViewModel.CurrentMediaPath);
    }

    private async Task ProbeDolbyVisionAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        string? probe = await Task.Run(() => HdrDiagnosticsService.ProbeDolbyVision(path));
        if (string.IsNullOrWhiteSpace(probe)) return;
        AppLogService.Information("DolbyVisionProbe", probe, AppMode.IsPrivacy
            ? new { FileName = System.IO.Path.GetFileName(path) }
            : new { MediaPath = path });
        // Profile 5 没有 HDR10 兼容基础层，系统媒体框架不会处理它的 IPT/RPU，画面必然发绿/发紫，
        // 因此这里直接给出可行出路：交给使用 libplacebo/MPCV 管线的外部播放器。
        string message = probe.Contains("Profile 5", StringComparison.Ordinal)
            ? "检测到杜比视界 Profile 5：系统媒体框架必然发绿/发紫，请在播放区域右键菜单开启「打开杜比引擎」（需 libmpv-2.dll）"
            : probe;
        DispatcherQueue.TryEnqueue(() => ViewModel.ShowNotification(message));
    }

    private async Task<IReadOnlyList<StorageFile>> PickMediaFileAsync()
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            WinRT.Interop.WindowNative.GetWindowHandle(this));
        // 与拖放扫描、文件关联共用同一份扩展名列表（Models\SupportedMedia.cs），
        // 避免文件选择器只提供其中一部分、与 README 声明的支持范围不一致。
        foreach (string extension in SupportedMedia.MediaExtensions)
            picker.FileTypeFilter.Add(extension);
        var files = await picker.PickMultipleFilesAsync();
        return files.ToList();
    }

    /// <summary>把界面主题（浅色/深色/跟随系统）应用到窗口根元素。</summary>
    private void ApplyThemeMode()
    {
        Root.RequestedTheme = settings.ThemeMode switch
        {
            1 => ElementTheme.Light,
            2 => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
    }

    private void ApplySettings()
    {
        double windowWidth = Root.ActualWidth > 0 ? Root.ActualWidth : 1200;
        double defaultWidth = windowWidth * 0.30;
        if (settings.LeftSidebarWidth <= 0) settings.LeftSidebarWidth = defaultWidth;
        if (settings.RightSidebarWidth <= 0) settings.RightSidebarWidth = defaultWidth;
        LeftSidebar.Width = ClampSidebarWidth(settings.LeftSidebarWidth);
        RightSidebar.Width = ClampSidebarWidth(settings.RightSidebarWidth);
        ViewModel.Volume = Math.Clamp(settings.Volume, 0, 100);
        foreach (Border panel in new[] { TopBar, LeftSidebar, RightSidebar, ControlBar })
        {
            if (panel.Background is BackdropBlurBrush brush)
            {
                brush.BlurAmount = Math.Clamp(settings.BlurAmount, 0, 100);
                brush.TintOpacity = Math.Clamp(settings.TintOpacity, 0, 1);
            }
        }
        ApplySubtitleLayout();
    }

    private void Root_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplySubtitleLayout(e.NewSize.Width, e.NewSize.Height);

    /// <summary>
    /// 字幕字号以 1200 像素宽的播放区域为基准进行缩放；垂直位置使用高度百分比，
    /// 从而在窗口化、最大化和全屏状态下保持一致的视觉比例。
    /// </summary>
    private void ApplySubtitleLayout(double? actualWidth = null, double? actualHeight = null)
    {
        double width = actualWidth.GetValueOrDefault(Root.ActualWidth);
        double height = actualHeight.GetValueOrDefault(Root.ActualHeight);
        if (width <= 0) width = 1200;
        if (height <= 0) height = 760;

        double baseFontSize = Math.Clamp(settings.SubtitleBaseFontSize, 12, 72);
        double scale = Math.Clamp(width / 1200.0, 0.60, 2.50);
        SubtitleTextBlock.FontSize = Math.Clamp(baseFontSize * scale, 10, 120);
        string fontFamily = string.IsNullOrWhiteSpace(settings.SubtitleFontFamily)
            ? "Segoe UI" : settings.SubtitleFontFamily;
        SubtitleTextBlock.FontFamily = new FontFamily(fontFamily);
        SubtitleTextBlock.MaxWidth = Math.Max(240, width * 0.84);

        double horizontalMargin = Math.Max(24, width * 0.06);
        double bottomPercent = Math.Clamp(settings.SubtitleBottomOffsetPercent, 2, 50);
        double bottomMargin = height * bottomPercent / 100.0;
        SubtitleOverlay.Margin = new Thickness(
            horizontalMargin, 0, horizontalMargin, bottomMargin);
    }

    private double ClampSidebarWidth(double width)
    {
        double windowWidth = Root.ActualWidth > 0 ? Root.ActualWidth : 1200;
        return Math.Clamp(width, 180, Math.Max(180, windowWidth * 0.60));
    }

    private void SidebarHandle_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement handle ||
            !e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed) return;
        activeSidebarHandle = handle;
        sidebarHandleStartPoint = e.GetCurrentPoint(Root).Position;
        sidebarHandleStartWidth = handle == LeftSidebarHandle ? LeftSidebar.Width : RightSidebar.Width;
        sidebarHandleMoved = false;
        handle.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void SidebarHandle_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        // 通过较小的移动阈值区分“单击关闭侧栏”和“拖动调整宽度”。
        if (sender is not FrameworkElement handle || activeSidebarHandle != handle ||
            !e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed) return;

        double delta = e.GetCurrentPoint(Root).Position.X - sidebarHandleStartPoint.X;
        if (!sidebarHandleMoved && Math.Abs(delta) < 3) return;
        sidebarHandleMoved = true;

        Border panel = handle == LeftSidebarHandle ? LeftSidebar : RightSidebar;
        if (!visiblePanels.Contains(panel)) SetPanelVisible(panel, true);
        ElementCompositionPreview.GetElementVisual(panel).StopAnimation("Translation");
        panel.Translation = Vector3.Zero;
        handle.Translation = Vector3.Zero;
        panel.IsHitTestVisible = true;

        panel.Width = ClampSidebarWidth(sidebarHandleStartWidth +
            (handle == LeftSidebarHandle ? delta : -delta));
        if (handle == LeftSidebarHandle) settings.LeftSidebarWidth = panel.Width;
        else settings.RightSidebarWidth = panel.Width;
        UpdateSidebarHandlePositions();
        e.Handled = true;
    }

    private void SidebarHandle_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement handle || activeSidebarHandle != handle) return;
        handle.ReleasePointerCapture(e.Pointer);
        if (!sidebarHandleMoved)
        {
            Border panel = handle == LeftSidebarHandle ? LeftSidebar : RightSidebar;
            SetPanelVisible(panel, !visiblePanels.Contains(panel));
        }
        else
        {
            settingsService.Save(settings);
        }
        activeSidebarHandle = null;
        e.Handled = true;
    }

    private void SidebarHandle_PointerCaptureLost(object sender, PointerRoutedEventArgs e) =>
        activeSidebarHandle = null;

    private void SidebarHandle_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is UIElement element) ProtectedCursorProperty?.SetValue(element, HorizontalResizeCursor);
    }

    private void SidebarHandle_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is UIElement element && activeSidebarHandle != element)
            ProtectedCursorProperty?.SetValue(element, null);
    }

    private void UpdateSidebarHandlePositions()
    {
        LeftSidebarHandle.Margin = new Thickness(
            Math.Max(0, LeftSidebar.Width - LeftSidebarHandle.Width / 2),
            52, 0, 112);
        RightSidebarHandle.Margin = new Thickness(0, 52,
            Math.Max(0, RightSidebar.Width - RightSidebarHandle.Width / 2),
            112);
    }

    /// <summary>
    /// 打开独立的设置窗口（不是浮在主窗口上的对话框：主窗口再小也不会挤掉设置项）。
    /// 已打开时只激活，避免出现多个设置窗口。
    /// </summary>
    private Task ShowSettingsAsync()
    {
        if (settingsWindow is not null)
        {
            settingsWindow.Activate();
            return Task.CompletedTask;
        }

        var window = new SettingsWindow(
            settings,
            () => settingsService.Save(settings),
            BuildHdrDiagnosticsText,
            ViewModel.ClearAllPlaybackHistoryAsync,
            ViewModel.ClearBothModesHistoryAsync);
        // 打开设置窗口时记下呈现模式，保存后只在它真正变化时重载当前媒体。
        settingsWindowDirectPresentBaseline = settings.HdrDirectPresent;
        window.ClosedByUser += (_, _) => settingsWindow = null;
        window.Saved += async (_, _) =>
        {
            try { await ApplySettingsFromWindowAsync(); }
            catch (Exception ex)
            {
                AppLogService.Error("ApplySettingsAfterSaveFailed", "应用设置失败", null, ex);
            }
        };
        settingsWindow = window;
        window.Activate();
        return Task.CompletedTask;
    }

    /// <summary>设置窗口点“保存更改”后，把改动应用到主窗口与播放管线。</summary>
    private async Task ApplySettingsFromWindowAsync()
    {
        if (FindKeepControlBarToggle() is ToggleButton controlBarToggle)
            controlBarToggle.IsChecked = settings.KeepControlBarVisible;
        ApplyThemeMode();
        ApplySettings();
        ApplyControlBarVisibilitySetting();
        // 视频呈现模式（自绘 / 直通）随选项生效；该开关只在媒体管线重建后才真正改变输出路径，
        // 因此切换后重新装载当前媒体。
        ViewModel.ApplyRenderModeSetting();
        ApplyVideoRenderMode();
        ViewModel.ApplyOwnSubtitleSetting();
        if (settingsWindowDirectPresentBaseline != settings.HdrDirectPresent)
            ViewModel.ReloadCurrentMediaForRenderMode();
        if (ViewModel.HasCurrentMedia) ReportHdrDiagnostics();
        ViewModel.RefreshSkipSettings();
        await ViewModel.ApplyPlaybackHistorySettingsAsync();
        UpdateSkipMarkers();
        ViewModel.ShowNotification(settings.ThemeMode switch
        {
            1 => "已保存设置并切换为浅色主题",
            2 => "已保存设置并切换为深色主题",
            _ => "已保存设置，主题跟随系统"
        });
    }

    private void Playlist_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        DependencyObject? current = source;
        while (current is not null && current is not ListViewItem)
            current = VisualTreeHelper.GetParent(current);
        if (current is not ListViewItem container || container.Content is not PlaylistItem item) return;

        var flyout = new MenuFlyout();
        FontIcon Icon(string glyph) => new()
        {
            Glyph = glyph,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons"),
            FontSize = 16
        };

        var play = new MenuFlyoutItem { Text = "播放", Icon = Icon("\uE768") };
        play.Click += (_, _) => ViewModel.PlayItem(item);
        flyout.Items.Add(play);

        var restart = new MenuFlyoutItem { Text = "从头播放…", Icon = Icon("\uE777") };
        restart.Click += async (_, _) =>
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = "从头播放？",
                Content = $"将清除“{item.Name}”的续播记录并从头开始播放。",
                PrimaryButtonText = "从头播放",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                await ViewModel.PlayFromBeginningAsync(item);
        };
        flyout.Items.Add(restart);

        var clear = new MenuFlyoutItem
        {
            Text = "清除此文件的播放记录…",
            Icon = Icon("\uE74D"),
            IsEnabled = item.HasPlaybackHistory
        };
        clear.Click += async (_, _) =>
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = "清除播放记录？",
                Content = $"将删除“{item.Name}”保存的续播位置。",
                PrimaryButtonText = "清除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                await ViewModel.ClearPlaybackHistoryAsync(item);
        };
        flyout.Items.Add(clear);
        flyout.Items.Add(new MenuFlyoutSeparator());

        flyout.ShowAt(container, e.GetPosition(container));
        e.Handled = true;
    }

    private void ToggleFullScreen()
    {
        if (isFullScreen)
        {
            isFullScreen = false;
            AppWindow.SetPresenter(AppWindowPresenterKind.Default);
            RestoreCursorAfterFullScreen();
            // 从全屏退回窗口化后，恢复“窗口总在最前”设置。
            ApplyAlwaysOnTopState();
        }
        else
        {
            EnterFullScreen();
        }
    }

    /// <summary>
    /// “窗口总在最前”开关（TopBar 上全屏按钮右侧）。全屏期间仅记录意向，
    /// 回到窗口化时由 <see cref="ApplyAlwaysOnTopState"/> 生效。
    /// </summary>
    private void AlwaysOnTopToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton toggleButton) return;
        keepOnTopRequested = toggleButton.IsChecked == true;
        settings.AlwaysOnTop = keepOnTopRequested;
        settingsService.Save(settings);
        ApplyAlwaysOnTopState();
        ViewModel.ShowNotification(keepOnTopRequested ? "窗口已置顶显示" : "已关闭窗口置顶");
    }

    private void ApplyAlwaysOnTopState()
    {
        if (isFullScreen) return;
        if (AppWindow.Presenter is not OverlappedPresenter presenter) return;
        try
        {
            presenter.IsAlwaysOnTop = keepOnTopRequested;
        }
        catch
        {
            // 某些呈现器状态不允许设置置顶，忽略即可。
        }
    }

    private void EnterFullScreen()
    {
        if (isFullScreen) return;
        isFullScreen = true;
        AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        // 进入全屏即重新计时并保持光标可见；之后无操作超时才自动隐藏。
        cursorHideDeadline = DateTime.UtcNow.AddSeconds(settings.AutoHideDelay);
        MakeCursorVisible();
    }

    /// <summary>通过 CursorManager 恢复鼠标指针显示。</summary>
    private void MakeCursorVisible()
    {
        fullScreenCursorHidden = false;
        cursorManager.Show();
    }

    /// <summary>退出全屏后强制恢复指针，避免用户看到“鼠标消失”的窗口化界面。</summary>
    private void RestoreCursorAfterFullScreen()
    {
        cursorHideDeadline = DateTime.MaxValue;
        MakeCursorVisible();
    }

    /// <summary>通过 CursorManager 隐藏鼠标指针（空闲超时后由 EnforceCursorAutoHide 调用）。</summary>
    private void HideCursorForFullScreen()
    {
        if (fullScreenCursorHidden) return;
        fullScreenCursorHidden = true;
        cursorManager.Hide();
    }

    /// <summary>
    /// 记录一次“有效”的鼠标操作：移动超过阈值、点击、滚动等。
    /// 刷新自动隐藏倒计时，指针若处于隐藏状态则恢复显示。
    /// </summary>
    private void NotifyCursorActivity()
    {
        if (!isFullScreen) return;
        cursorHideDeadline = DateTime.UtcNow.AddSeconds(settings.AutoHideDelay);
        if (fullScreenCursorHidden) MakeCursorVisible();
    }

    /// <summary>
    /// 判断本次指针移动是否“足够大”（超过 3 物理像素）。鼠标传感器在静止时也会产生
    /// 1~2 像素的微抖动，若把这些都算作活动，自动隐藏将永远等不到空闲计时，指针也就
    /// 始终不隐藏。
    /// </summary>
    private bool IsSignificantPointerMove()
    {
        if (!GetCursorPos(out NativePoint current)) return false;
        if (!cursorActivityAnchorSet)
        {
            cursorActivityAnchor = current;
            cursorActivityAnchorSet = true;
            return true;
        }
        int dx = current.X - cursorActivityAnchor.X;
        int dy = current.Y - cursorActivityAnchor.Y;
        if (dx * dx + dy * dy < 9) return false;
        cursorActivityAnchor = current;
        return true;
    }

    /// <summary>由面板自动隐藏计时器周期调用：全屏播放空闲超时后隐藏指针（并连带隐藏悬浮栏）。</summary>
    private void EnforceCursorAutoHide(DateTime now)
    {
        // 只有“全屏 + 窗口激活 + 正在播放 + 指针空闲超时”才自动隐藏；
        // 窗口未激活（如 Alt+Tab）、指针已离开窗口、或暂停/停止时不隐藏，
        // 否则会把光标从其他窗口上“藏掉”或让用户在暂停时找不到指针。
        if (!isFullScreen || !windowActive || !isPlaybackActive || now < cursorHideDeadline) return;
        if (fullScreenCursorHidden) return;
        HideCursorForFullScreen();
        // 与面板逻辑保持一致：鼠标长时间无操作时把已显示的悬浮栏一并隐藏。
        // 但指针正停在某个面板上时不隐藏它——悬停必须优先于空闲隐藏。
        DateTime deadline = now.AddSeconds(settings.AutoHideDelay);
        foreach (FrameworkElement panel in visiblePanels)
            if ((panel != ControlBar || !settings.KeepControlBarVisible) &&
                !hoveredPanels.Contains(panel))
                panelHideDeadlines[panel] = deadline;
    }

    private void PositionSlider_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        isDraggingPosition = true;
        ViewModel.BeginSeek();
        ViewModel.Position = PositionSlider.Value;
        ViewModel.ShowChrome();
    }

    private void PositionSlider_ValueChanged(object sender,
        Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (isDraggingPosition) ViewModel.Position = e.NewValue;
    }

    private void PositionSlider_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!isDraggingPosition) return;
        ViewModel.Position = PositionSlider.Value;
        ViewModel.EndSeek();
        isDraggingPosition = false;
    }

    private void SkipMarker_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement marker) return;
        draggedSkipMarker = marker;
        marker.CapturePointer(e.Pointer);
        UpdateDraggedSkipMarker(e, false);
        e.Handled = true;
    }

    private void SkipMarker_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!ReferenceEquals(draggedSkipMarker, sender) || !e.GetCurrentPoint((UIElement)sender).Properties.IsLeftButtonPressed) return;
        UpdateDraggedSkipMarker(e, false);
        e.Handled = true;
    }

    private void SkipMarker_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!ReferenceEquals(draggedSkipMarker, sender)) return;
        UpdateDraggedSkipMarker(e, true);
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);
        draggedSkipMarker = null;
        e.Handled = true;
    }

    private void UpdateDraggedSkipMarker(PointerRoutedEventArgs e, bool save)
    {
        if (draggedSkipMarker is null || PositionSlider.ActualWidth <= 0) return;
        double x = e.GetCurrentPoint(PositionSlider).Position.X;
        double seconds = Math.Clamp(x / PositionSlider.ActualWidth, 0, 1) * ViewModel.Duration;
        if (draggedSkipMarker == IntroMarker)
            ViewModel.SetIntroMarkerPosition(seconds, save);
        else
            ViewModel.SetOutroMarkerPosition(seconds, save);
        UpdateSkipMarkers();
        ViewModel.ShowChrome();
    }

    private void Root_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        // 只在指针发生“明显移动”（>3px）时视为活动，过滤静止时传感器的微抖动。
        if (IsSignificantPointerMove()) NotifyCursorActivity();
        if (isDraggingWindow && GetCursorPos(out NativePoint cursor))
        {
            int deltaX = cursor.X - windowDragStartCursor.X;
            int deltaY = cursor.Y - windowDragStartCursor.Y;
            if (!windowDragMoved && deltaX * deltaX + deltaY * deltaY >= 25)
                windowDragMoved = true;
            if (windowDragMoved && !isFullScreen)
                AppWindow.Move(new Windows.Graphics.PointInt32(
                    windowDragStartPosition.X + deltaX,
                    windowDragStartPosition.Y + deltaY));
            e.Handled = true;
            return;
        }

        Windows.Foundation.Point point = e.GetCurrentPoint(Root).Position;
        double width = Root.ActualWidth;
        double height = Root.ActualHeight;
        bool middleY = point.Y > 52 && point.Y < height - 112;

        UpdatePanelHover(TopBar, point.Y <= 52);
        UpdatePanelHover(ControlBar, point.Y >= height - 112);
        if (settings.AutoShowSidebar)
        {
            UpdatePanelHover(LeftSidebar, middleY && point.X <= (visiblePanels.Contains(LeftSidebar) ? LeftSidebar.Width : 36));
            UpdatePanelHover(RightSidebar, middleY && point.X >= width - (visiblePanels.Contains(RightSidebar) ? RightSidebar.Width : 36));
        }
    }

    private void Root_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        // 指针离开窗口后停止自动隐藏计时，防止把光标从其他应用窗口上隐藏；
        // 移回窗口时 PointerMoved 会重新开始计时并恢复光标。
        if (isFullScreen) cursorHideDeadline = DateTime.MaxValue;
        // 指针已不在窗口内，任何面板都不再算悬停。
        hoveredPanels.Clear();
        DateTime deadline = DateTime.UtcNow.AddSeconds(settings.AutoHideDelay);
        foreach (FrameworkElement panel in visiblePanels)
            if (panel != ControlBar || !settings.KeepControlBarVisible)
                panelHideDeadlines[panel] = deadline;
    }

    private void Root_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        NotifyCursorActivity();
        if (e.Handled) return;
        // 每次打开时动态构建菜单，使图标和选中状态与当前播放状态一致。
        var flyout = new MenuFlyout();

        FontIcon MenuIcon(string glyph) => new()
        {
            Glyph = glyph,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons"),
            FontSize = 16
        };

        MenuFlyoutItem CommandItem(string text, System.Windows.Input.ICommand command, string glyph)
        {
            var item = new MenuFlyoutItem { Text = text, Icon = MenuIcon(glyph) };
            item.Click += (_, _) =>
            {
                if (command.CanExecute(null)) command.Execute(null);
            };
            return item;
        }

        MenuFlyoutItem ActionItem(string text, Action action, string glyph)
        {
            var item = new MenuFlyoutItem { Text = text, Icon = MenuIcon(glyph) };
            item.Click += (_, _) => action();
            return item;
        }

        flyout.Items.Add(CommandItem("打开文件…", ViewModel.OpenFileCommand, "\uE8E5"));
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(CommandItem("播放 / 暂停", ViewModel.PlayPauseCommand, ViewModel.PlayPauseGlyph));
        flyout.Items.Add(ActionItem("停止", ViewModel.StopPlayback, "\uE71A"));
        flyout.Items.Add(ActionItem("重新播放", ViewModel.RestartPlayback, "\uE777"));
        flyout.Items.Add(CommandItem("上一项", ViewModel.PreviousCommand, "\uE892"));
        flyout.Items.Add(CommandItem("下一项", ViewModel.NextCommand, "\uE893"));

        var jump = new MenuFlyoutSubItem { Text = "跳转", Icon = MenuIcon("\uE72A") };
        foreach ((string text, double seconds) in new[]
        {
            ("后退 1 秒", -1d), ("前进 1 秒", 1d), ("后退 5 秒", -5d), ("前进 5 秒", 5d),
            ("后退 1 分钟", -60d), ("前进 1 分钟", 60d),
            ("后退 5 分钟", -300d), ("前进 5 分钟", 300d)
        }) jump.Items.Add(ActionItem(text, () => ViewModel.SeekRelative(seconds),
            seconds < 0 ? "\uE76B" : "\uE76C"));
        flyout.Items.Add(jump);

        var rate = new MenuFlyoutSubItem { Text = "播放速度", Icon = MenuIcon("\uE823") };
        foreach (double value in new[] { 0.5, 0.75, 1.0, 1.25, 1.5, 2.0 })
            rate.Items.Add(ActionItem($"{value:0.##}×", () => ViewModel.SetPlaybackRate(value), "\uE823"));
        flyout.Items.Add(rate);

        var loop = new ToggleMenuFlyoutItem
        {
            Text = "单曲循环",
            IsChecked = ViewModel.IsLoopingEnabled,
            Icon = MenuIcon("\uE8EE")
        };
        loop.Click += (_, _) => ViewModel.IsLoopingEnabled = loop.IsChecked;
        flyout.Items.Add(loop);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(CommandItem("显示 / 隐藏媒体栏", ViewModel.ToggleFoldersCommand, "\uE8B7"));
        flyout.Items.Add(CommandItem("显示 / 隐藏播放列表", ViewModel.TogglePlaylistCommand, "\uE8FD"));
        flyout.Items.Add(CommandItem("全屏", ViewModel.FullScreenCommand, "\uE740"));
        // libmpv 引擎：系统媒体框架无法正确呈现杜比视界 Profile 5 时的替代播放引擎。
        var mpvEngineItem = new ToggleMenuFlyoutItem
        {
            Text = "打开杜比引擎",
            IsChecked = settings.UseLibMpvEngine,
            IsEnabled = ViewModel.IsMpvEngineAvailable,
            Icon = MenuIcon("\uE7F4")
        };
        mpvEngineItem.Click += (_, _) =>
        {
            settings.UseLibMpvEngine = mpvEngineItem.IsChecked;
            settingsService.Save(settings);
            if (mpvEngineItem.IsChecked)
            {
                // 立即把当前媒体切到 libmpv，便于直接对比画面。
                ViewModel.SwitchCurrentMediaToMpvEngine();
            }
            else
            {
                ViewModel.StopMpvPlayback();
                if (ViewModel.SelectedItem is { } item) ViewModel.PlayItem(item);
            }
        };
        flyout.Items.Add(mpvEngineItem);
        //flyout.Items.Add(ActionItem("用外部播放器打开", () => ViewModel.ShowNotification(
        //    ExternalPlayerService.Open(ViewModel.CurrentMediaPath, settings.ExternalPlayerPath)), "\uE8E5"));
        flyout.Items.Add(CommandItem("选项…", ViewModel.SettingsCommand, "\uE713"));
        flyout.Items.Add(new MenuFlyoutSeparator());
        // 菜单项回调里同步调用 Window.Close() 会在弹出菜单轻触消失的过程中抛
        // COMException 0x80004004；这里等菜单完全消失后再退出，并带强制结束进程的兜底。
        flyout.Items.Add(ActionItem("退出", () =>
        {
            var exitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            exitTimer.Tick += (timer, _) =>
            {
                if (timer is DispatcherTimer dispatcherTimer) dispatcherTimer.Stop();
                ExitApplication();
            };
            exitTimer.Start();
        }, "\uE8BB"));

        flyout.ShowAt(Root, e.GetPosition(Root));
        e.Handled = true;
    }

    private void Border_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border border) return;

        if (border.Name.Contains("Center"))
        {
            SetMarkerScale(sender, 1.25);
        }

    }

    private void Border_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border border) return;

        if (border.Name.Contains("Center"))
        {
            SetMarkerScale(sender, 1.0);
        }
    }
    private static void SetMarkerScale(object sender, double scale)
    {
        if (sender is Border { RenderTransform: ScaleTransform transform })
        {
            transform.ScaleX = scale;
            transform.ScaleY = scale;
        }
    }
}
