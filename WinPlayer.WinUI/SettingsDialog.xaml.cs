using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.IO;
using System.Linq;
using Windows.ApplicationModel.DataTransfer;
using Windows.Globalization;
using Windows.System;
using WinPlayer.WinUI.Models;
using WinPlayer.WinUI.Services;

namespace WinPlayer.WinUI;

/// <summary>
/// 独立的播放器设置对话框。界面只编辑控件中的临时值，用户确认后再写回配置对象。
/// </summary>
public sealed partial class SettingsDialog : ContentDialog
{
    public SettingsDialog(PlayerSettings settings)
    {
        InitializeComponent();
        PopulateSubtitleFonts(settings.SubtitleFontFamily);
        LoadSettings(settings);
        SettingsNavigation.SelectedItem = PlaybackNavigationItem;
        ShowSection("Playback");
    }

    public void ApplyTo(PlayerSettings settings)
    {
        settings.SingleInstance = SingleInstanceToggle.IsOn;
        settings.AutoPlay = AutoPlayToggle.IsOn;
        settings.AutoFullScreen = AutoFullScreenToggle.IsOn;
        settings.KeepControlBarVisible = KeepControlBarToggle.IsOn;
        settings.EnableVolumeFadeIn = VolumeFadeToggle.IsOn;
        settings.SavePlaybackPosition = SavePositionToggle.IsOn;
        settings.ResumePlaybackPosition = ResumePositionToggle.IsOn;

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
        KeepControlBarToggle.IsOn = settings.KeepControlBarVisible;
        VolumeFadeToggle.IsOn = settings.EnableVolumeFadeIn;
        SavePositionToggle.IsOn = settings.SavePlaybackPosition;
        ResumePositionToggle.IsOn = settings.ResumePlaybackPosition;

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

    private async System.Threading.Tasks.Task RefreshLogAsync()
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
