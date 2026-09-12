using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Globalization;
using Windows.System;
using Windows.UI;
using WinPlayer.WinUI.Models;
using WinPlayer.WinUI.Services;

namespace WinPlayer.WinUI;

/// <summary>
/// 独立的播放器设置窗口。
/// </summary>
public sealed partial class SettingsWindow : Window
{
    private readonly FileAssociationService fileAssociationService = new();
    private readonly PlayerSettings settings;
    private readonly Action persistSettings;
    private readonly Func<string>? runHdrDiagnostics;
    private readonly Func<Task>? clearCurrentModeHistory;
    private readonly Func<Task>? clearBothModesHistory;
    private readonly List<(string Extension, CheckBox Box)> extensionChecks = new();

    /// <summary>用户点击“保存更改”并已写回配置后触发，供主窗口应用设置。</summary>
    public event EventHandler? Saved;

    /// <summary>窗口关闭后触发，供主窗口清理引用。</summary>
    public event EventHandler? ClosedByUser;

    public SettingsWindow(PlayerSettings settings, Action persistSettings,
        Func<string>? runHdrDiagnostics = null,
        Func<Task>? clearCurrentModeHistory = null,
        Func<Task>? clearBothModesHistory = null)
    {
        this.settings = settings;
        this.persistSettings = persistSettings;
        this.runHdrDiagnostics = runHdrDiagnostics;
        this.clearCurrentModeHistory = clearCurrentModeHistory;
        this.clearBothModesHistory = clearBothModesHistory;
        InitializeComponent();
        // WinUI 的 Window.OnClosed 不可重写，用 Closed 事件代替。
        Closed += SettingsWindow_Closed;

        string modeName = AppMode.IsPrivacy ? "隐私模式" : "普通模式";
        //string settingsFile = AppMode.IsPrivacy ? "settings.b.json" : "settings.json";
        //Title = $"播放器选项 — {modeName}";
        //ModeIndicatorText.Text = $"当前模式：{modeName}　设置保存到 {settingsFile}";
        ModeChipText.Text = modeName;
        ModeChipIcon.Glyph = AppMode.IsPrivacy ? "\uED1A" : "\uE7F4";
        SettingsRoot.RequestedTheme = ToElementTheme(settings.ThemeMode);
        ApplyWindowIdentity();
        ApplyCustomTitleBar();

        PopulateSubtitleFonts(settings.SubtitleFontFamily);
        LoadSettings(settings);
        PopulateExtensionOptions();
        SettingsNavigation.SelectedItem = PlaybackNavigationItem;
        ShowSection("Playback");
        RefreshAssociationStatus();
        RefreshClearHistoryHint();
    }

    /// <summary>窗口尺寸按当前显示器 DPI 缩放，保证在高分屏上也不是一小块。</summary>
    private void ApplyWindowIdentity()
    {
        try
        {
            // 图标与主窗口保持同一模式，便于在任务栏里区分。
            string iconPath = System.IO.Path.Combine(
                AppContext.BaseDirectory, "Images", AppMode.IconFileName);
            if (File.Exists(iconPath)) AppWindow.SetIcon(iconPath);

            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            double scale = GetDpiForWindow(hwnd) is > 0 and var dpi ? dpi / 96.0 : 1.0;
            AppWindow.Resize(new Windows.Graphics.SizeInt32(
                (int)Math.Round(960 * scale), (int)Math.Round(720 * scale)));
        }
        catch (Exception ex)
        {
            AppLogService.Warning("SettingsWindowSetupFailed", "设置窗口初始化尺寸或图标失败", null, ex);
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>
    /// 自定义标题栏：与主窗口一致（内容延伸到标题栏 + 透明按钮底色 + 白色按钮字形），
    /// </summary>
    private void ApplyCustomTitleBar()
    {
        try
        {
            ExtendsContentIntoTitleBar = true;
            AppWindow.TitleBar.ExtendsContentIntoTitleBar = true;
            AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
            AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            // 顶部条始终是深色 chrome，因此按钮字形固定为白色，并在悬停时轻微提亮。
            AppWindow.TitleBar.ButtonForegroundColor = Colors.White;
            AppWindow.TitleBar.ButtonInactiveForegroundColor = Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF);
            AppWindow.TitleBar.ButtonHoverForegroundColor = Colors.White;
            AppWindow.TitleBar.ButtonHoverBackgroundColor = Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF);
            AppWindow.TitleBar.ButtonPressedBackgroundColor = Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF);
            SetTitleBar(SettingsTitleBar);
        }
        catch (Exception ex)
        {
            AppLogService.Warning("SettingsWindowTitleBarFailed", "设置自定义标题栏失败", null, ex);
        }
    }

    private static ElementTheme ToElementTheme(int themeMode) => themeMode switch
    {
        1 => ElementTheme.Light,
        2 => ElementTheme.Dark,
        _ => ElementTheme.Default
    };

    private void ThemeModeRadio_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        SettingsRoot.RequestedTheme = ToElementTheme(ThemeModeRadio.SelectedIndex);

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyTo(settings);
        persistSettings();
        Saved?.Invoke(this, EventArgs.Empty);
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();

    private void SettingsWindow_Closed(object sender, WindowEventArgs args) =>
        ClosedByUser?.Invoke(this, EventArgs.Empty);

    private void RefreshClearHistoryHint() =>
        ClearHistoryStatusText.Text = AppMode.IsPrivacy
            ? "当前处于隐私模式：“清空当前模式的记录”只会清空隐私记录（history.b.json）。"
            : "当前处于普通模式：“清空当前模式的记录”只会清空普通记录（playback-history.json）。";

    private async void ClearCurrentModeHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (clearCurrentModeHistory is null) return;
        string modeName = AppMode.IsPrivacy ? "隐私模式" : "普通模式";
        bool confirmed = await ConfirmAsync(
            $"清空{modeName}的播放记录？",
            $"将删除{modeName}下所有文件保存的续播位置；另一种模式的记录、设置与播放列表都不受影响。",
            "清空");
        if (!confirmed) return;

        ClearCurrentModeButton.IsEnabled = false;
        try
        {
            await clearCurrentModeHistory();
            ClearHistoryStatusText.Text = $"{DateTime.Now:HH:mm:ss}　已清空{modeName}的播放记录。";
        }
        catch (Exception ex)
        {
            ClearHistoryStatusText.Text = "清空失败：" + ex.Message;
            AppLogService.Error("ClearPlaybackHistoryFailed", "清空播放记录失败", null, ex);
        }
        finally
        {
            ClearCurrentModeButton.IsEnabled = true;
        }
    }

    private async void ClearBothModesHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (clearBothModesHistory is null) return;
        bool confirmed = await ConfirmAsync(
            "清空两种模式的全部记录？",
            "将同时删除普通模式与隐私模式保存的所有续播位置（两个记录文件都会被清空）；设置与播放列表不受影响。",
            "两种模式都清空");
        if (!confirmed) return;

        ClearBothModesButton.IsEnabled = false;
        try
        {
            await clearBothModesHistory();
            ClearHistoryStatusText.Text = $"{DateTime.Now:HH:mm:ss}　已清空两种模式的播放记录。";
        }
        catch (Exception ex)
        {
            ClearHistoryStatusText.Text = "清空失败：" + ex.Message;
            AppLogService.Error("ClearPlaybackHistoryFailed", "清空两种模式播放记录失败", null, ex);
        }
        finally
        {
            ClearBothModesButton.IsEnabled = true;
        }
    }

    private async Task<bool> ConfirmAsync(string title, string content, string primaryText)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = SettingsRoot.XamlRoot,
            Title = title,
            Content = content,
            PrimaryButtonText = primaryText,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private void PopulateExtensionOptions()
    {
        var selected = new HashSet<string>(
            settings.AssociatedExtensions ?? new List<string>(),
            StringComparer.OrdinalIgnoreCase);
        ExtensionList.Children.Clear();
        extensionChecks.Clear();
        foreach (string extension in SupportedMedia.MediaExtensions)
        {
            var box = new CheckBox
            {
                Content = extension,
                IsChecked = selected.Contains(extension),
                MinWidth = 0
            };
            AutomationProperties.SetName(box, $"关联 {extension}");
            ExtensionList.Children.Add(box);
            extensionChecks.Add((extension, box));
        }
    }

    private List<string> GetCheckedExtensions() => extensionChecks
        .Where(item => item.Box.IsChecked == true)
        .Select(item => item.Extension)
        .ToList();

    private void SelectAllExtensionsButton_Click(object sender, RoutedEventArgs e)
    {
        foreach ((_, CheckBox box) in extensionChecks) box.IsChecked = true;
    }

    private void ClearAllExtensionsButton_Click(object sender, RoutedEventArgs e)
    {
        foreach ((_, CheckBox box) in extensionChecks) box.IsChecked = false;
    }

    private void RefreshAssociationStatus()
    {
        try
        {
            AssociationStatusText.Text = fileAssociationService.IsRegistered()
                ? $"已注册文件关联（{fileAssociationService.CountRegisteredExtensions()} 个扩展名），命令指向当前程序路径。"
                : "尚未注册文件关联。";
        }
        catch (Exception ex)
        {
            AssociationStatusText.Text = "读取关联状态失败：" + ex.Message;
        }
    }

    private async void RegisterAssociationButton_Click(object sender, RoutedEventArgs e)
    {
        if (AppMode.IsPrivacy)
        {
            AssociationStatusText.Text = "隐私模式不修改系统文件关联，请用普通模式注册。";
            return;
        }

        List<string> selected = GetCheckedExtensions();
        if (selected.Count == 0)
        {
            AssociationStatusText.Text = "请至少勾选一个扩展名再注册。";
            return;
        }

        try
        {
            // 注册前先保存用户选择，即使随后关闭窗口也不丢失。
            settings.AssociatedExtensions = selected;
            persistSettings();
            await Task.Run(() => fileAssociationService.Register(selected));
            AssociationStatusText.Text =
                $"已注册 {selected.Count} 个扩展名的“打开方式”。" +
                "如需双击直接打开，请在系统默认应用中选择本播放器。";
            AppLogService.Information("FileAssociationRegistered", "已注册系统文件关联",
                new { ExePath = fileAssociationService.ExePath, ExtensionCount = selected.Count });
        }
        catch (Exception ex)
        {
            AssociationStatusText.Text = "注册文件关联失败：" + ex.Message;
            AppLogService.Error("FileAssociationRegisterFailed", "注册系统文件关联失败",
                new { ExePath = fileAssociationService.ExePath }, ex);
        }
    }

    private async void UnregisterAssociationButton_Click(object sender, RoutedEventArgs e)
    {
        // 同上：移除关联同样是写注册表，隐私模式下不执行。
        if (AppMode.IsPrivacy)
        {
            AssociationStatusText.Text = "隐私模式不修改系统文件关联，请用普通模式移除。";
            return;
        }

        try
        {
            await Task.Run(() => fileAssociationService.Unregister());
            AssociationStatusText.Text =
                "已移除本程序的文件关联。之前已设为默认程序的扩展名将提示重新选择应用。";
            AppLogService.Information("FileAssociationUnregistered", "已移除系统文件关联");
        }
        catch (Exception ex)
        {
            AssociationStatusText.Text = "移除文件关联失败：" + ex.Message;
            AppLogService.Error("FileAssociationUnregisterFailed", "移除系统文件关联失败", ex);
        }
    }

    private async void OpenDefaultAppsSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            bool opened = await Launcher.LaunchUriAsync(new Uri("ms-settings:defaultapps"));
            if (!opened)
                AppLogService.Warning("DefaultAppsSettingsOpenFailed", "系统未能打开默认应用设置页");
        }
        catch (Exception ex)
        {
            AssociationStatusText.Text = "无法打开系统默认应用设置：" + ex.Message;
            AppLogService.Error("DefaultAppsSettingsOpenFailed", "打开系统默认应用设置失败", exception: ex);
        }
    }

    public void ApplyTo(PlayerSettings settings)
    {
        settings.SingleInstance = SingleInstanceToggle.IsOn;
        // 扩展名勾选结果随“保存更改”写回；注册动作另行点击“注册关联”执行。
        settings.AssociatedExtensions = GetCheckedExtensions();
        settings.AutoPlay = AutoPlayToggle.IsOn;
        settings.AutoFullScreen = AutoFullScreenToggle.IsOn;
        settings.ClickToPlayPause = ClickToPlayPauseToggle.IsOn;
        settings.HdrDirectPresent = HdrDirectPresentToggle.IsOn;
        settings.UseLibMpvEngine = UseLibMpvEngineToggle.IsOn;
        settings.HideOwnSubtitles = !OwnSubtitlesToggle.IsOn;
        settings.ExternalPlayerPath = ExternalPlayerPathBox.Text.Trim();
        settings.KeepControlBarVisible = KeepControlBarToggle.IsOn;
        settings.ThemeMode = ThemeModeRadio.SelectedIndex is >= 0 and <= 2
            ? ThemeModeRadio.SelectedIndex
            : 0;
        settings.EnableVolumeFadeIn = VolumeFadeToggle.IsOn;
        settings.SavePlaybackPosition = SavePositionToggle.IsOn;
        settings.ResumePlaybackPosition = ResumePositionToggle.IsOn;
        settings.AutoShowSidebar = SetAtuoSlideToggle.IsOn;

        settings.Volume = ReadNumber(VolumeNumber, settings.Volume, 0, 100);
        settings.VolumeFadeInDuration = ReadNumber(
            VolumeFadeDurationNumber, settings.VolumeFadeInDuration, 0.1, 10);
        settings.SeekSeconds = ReadNumber(SeekSecondsNumber, settings.SeekSeconds, 1, 300);
        settings.SkipIntroSeconds = ReadNumber(SkipIntroNumber, settings.SkipIntroSeconds, 0, 3600);
        settings.SkipOutroSeconds = ReadNumber(SkipOutroNumber, settings.SkipOutroSeconds, 0, 3600);
        settings.AutoHideDelay = ReadNumber(AutoHideDelayNumber, settings.AutoHideDelay, 0.5, 30);
        settings.AnimationDuration = ReadNumber(
            AnimationDurationNumber, settings.AnimationDuration, 100, 1500);
        settings.LeftSidebarWidth = ReadNumber(
            LeftSidebarWidthNumber, settings.LeftSidebarWidth, 180, 600);
        settings.RightSidebarWidth = ReadNumber(
            RightSidebarWidthNumber, settings.RightSidebarWidth, 180, 600);
        settings.BlurAmount = ReadNumber(BlurAmountNumber, settings.BlurAmount, 0, 100);
        settings.TintOpacity = ReadNumber(TintOpacityNumber, settings.TintOpacity, 0, 1);
        settings.SubtitleBaseFontSize = ReadNumber(
            SubtitleBaseFontSizeNumber, settings.SubtitleBaseFontSize, 12, 72);
        settings.SubtitleBottomOffsetPercent = ReadNumber(
            SubtitleBottomOffsetNumber, settings.SubtitleBottomOffsetPercent, 2, 50);
        settings.SubtitleFontFamily = SubtitleFontFamilyCombo.SelectedItem as string
            ?? settings.SubtitleFontFamily;
        settings.HistoryRetentionDays = (int)Math.Round(ReadNumber(
            HistoryRetentionNumber, settings.HistoryRetentionDays, 1, 3650));
        settings.MaxPlaybackHistoryEntries = (int)Math.Round(ReadNumber(
            MaxHistoryEntriesNumber, settings.MaxPlaybackHistoryEntries, 100, 100000));
    }

    private void LoadSettings(PlayerSettings settings)
    {
        SingleInstanceToggle.IsOn = settings.SingleInstance;
        AutoPlayToggle.IsOn = settings.AutoPlay;
        AutoFullScreenToggle.IsOn = settings.AutoFullScreen;
        ClickToPlayPauseToggle.IsOn = settings.ClickToPlayPause;
        HdrDirectPresentToggle.IsOn = settings.HdrDirectPresent;
        OwnSubtitlesToggle.IsOn = !settings.HideOwnSubtitles;
        UseLibMpvEngineToggle.IsOn = settings.UseLibMpvEngine;
        ExternalPlayerPathBox.Text = settings.ExternalPlayerPath ?? string.Empty;
        KeepControlBarToggle.IsOn = settings.KeepControlBarVisible;
        ThemeModeRadio.SelectedIndex = Math.Clamp(settings.ThemeMode, 0, 2);
        VolumeFadeToggle.IsOn = settings.EnableVolumeFadeIn;
        SavePositionToggle.IsOn = settings.SavePlaybackPosition;
        ResumePositionToggle.IsOn = settings.ResumePlaybackPosition;
        SetAtuoSlideToggle.IsOn = settings.AutoShowSidebar;

        VolumeNumber.Value = settings.Volume;
        VolumeFadeDurationNumber.Value = settings.VolumeFadeInDuration;
        SeekSecondsNumber.Value = settings.SeekSeconds;
        SkipIntroNumber.Value = settings.SkipIntroSeconds;
        SkipOutroNumber.Value = settings.SkipOutroSeconds;
        AutoHideDelayNumber.Value = settings.AutoHideDelay;
        AnimationDurationNumber.Value = settings.AnimationDuration;
        LeftSidebarWidthNumber.Value = Math.Clamp(settings.LeftSidebarWidth, 180, 600);
        RightSidebarWidthNumber.Value = Math.Clamp(settings.RightSidebarWidth, 180, 600);
        BlurAmountNumber.Value = settings.BlurAmount;
        TintOpacityNumber.Value = settings.TintOpacity;
        SubtitleBaseFontSizeNumber.Value = settings.SubtitleBaseFontSize;
        SubtitleBottomOffsetNumber.Value = settings.SubtitleBottomOffsetPercent;
        HistoryRetentionNumber.Value = settings.HistoryRetentionDays;
        MaxHistoryEntriesNumber.Value = settings.MaxPlaybackHistoryEntries;
    }

    private void PopulateSubtitleFonts(string configuredFont)
    {
        string[] fonts;
        try
        {
            fonts = CanvasTextFormat.GetSystemFontFamilies(ApplicationLanguages.Languages)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
        catch
        {
            fonts = new[] { "Segoe UI" };
        }

        SubtitleFontFamilyCombo.ItemsSource = fonts;
        string selected = fonts.FirstOrDefault(name =>
            string.Equals(name, configuredFont, StringComparison.CurrentCultureIgnoreCase))
            ?? fonts.FirstOrDefault(name => name == "Segoe UI")
            ?? fonts.FirstOrDefault()
            ?? "Segoe UI";
        SubtitleFontFamilyCombo.SelectedItem = selected;
    }

    private void SettingsNavigation_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is string section)
        {
            ShowSection(section);
            if (section == "Diagnostics") _ = RefreshLogAsync();
        }
    }

    private void ShowSection(string section)
    {
        PlaybackSection.Visibility = section == "Playback"
            ? Visibility.Visible : Visibility.Collapsed;
        AppearanceSection.Visibility = section == "Appearance"
            ? Visibility.Visible : Visibility.Collapsed;
        HistorySection.Visibility = section == "History"
            ? Visibility.Visible : Visibility.Collapsed;
        DiagnosticsSection.Visibility = section == "Diagnostics"
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task RefreshLogAsync()
    {
        LogStatusText.Text = "正在读取…";
        try
        {
            LogTextBox.Text = await AppLogService.ReadRecentAsync();
            LogStatusText.Text = $"更新于 {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            LogStatusText.Text = "读取失败";
            AppLogService.Error("LogViewerReadFailed", "程序内读取日志失败", exception: ex);
        }
    }

    private async void RefreshLogButton_Click(object sender, RoutedEventArgs e) =>
        await RefreshLogAsync();

    /// <summary>运行一次 HDR/杜比诊断：结果直接显示在上方文本框，同时已写入日志。</summary>
    private void RunHdrDiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        if (runHdrDiagnostics is null)
        {
            HdrDiagnosticsTextBox.Text = "当前窗口未提供诊断能力。";
            return;
        }

        try
        {
            HdrDiagnosticsTextBox.Text = runHdrDiagnostics();
        }
        catch (Exception ex)
        {
            HdrDiagnosticsTextBox.Text = "诊断失败：" + ex.Message;
            AppLogService.Error("HdrDiagnosticsFailed", "运行 HDR/杜比诊断失败", null, ex);
        }
    }

    private void CopyLogButton_Click(object sender, RoutedEventArgs e)
    {
        var package = new DataPackage();
        package.SetText(LogTextBox.Text ?? string.Empty);
        Clipboard.SetContent(package);
        Clipboard.Flush();
        LogStatusText.Text = "已复制";
    }

    private async void OpenLogFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppLogService.LogDirectory);
            bool opened = await Launcher.LaunchFolderPathAsync(AppLogService.LogDirectory);
            LogStatusText.Text = opened ? "已打开日志目录" : "无法打开日志目录";
            if (!opened)
                AppLogService.Warning("LogFolderOpenFailed", "系统未能打开日志目录",
                    new { AppLogService.LogDirectory });
        }
        catch (Exception ex)
        {
            LogStatusText.Text = "打开目录失败";
            AppLogService.Error("LogFolderOpenFailed", "打开日志目录失败",
                new { AppLogService.LogDirectory }, ex);
        }
    }

    private static double ReadNumber(NumberBox numberBox, double fallback,
        double minimum, double maximum)
    {
        double value = double.IsNaN(numberBox.Value) ? fallback : numberBox.Value;
        return Math.Clamp(value, minimum, maximum);
    }
}
