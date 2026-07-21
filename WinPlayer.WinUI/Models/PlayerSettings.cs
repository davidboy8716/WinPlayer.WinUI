namespace WinPlayer.WinUI.Models;

/// <summary>
/// 可序列化的用户首选项。旧版配置文件缺少新增属性时，属性初始化值同时作为默认值。
/// </summary>
public sealed class PlayerSettings
{
    public bool SingleInstance { get; set; } = true;
    public bool AutoPlay { get; set; } = true;
    public bool AutoFullScreen { get; set; }
    public double SeekSeconds { get; set; } = 3;
    public double LeftSidebarWidth { get; set; }
    public double RightSidebarWidth { get; set; }
    public double AnimationDuration { get; set; } = 360;
    public double AutoHideDelay { get; set; } = 3;
    public bool KeepControlBarVisible { get; set; }
    public double BlurAmount { get; set; } = 30;
    public double TintOpacity { get; set; } = 0.42;
    public double SubtitleBaseFontSize { get; set; } = 24;
    public double SubtitleBottomOffsetPercent { get; set; } = 17;
    public string SubtitleFontFamily { get; set; } = "Segoe UI";
    public double Volume { get; set; } = 80;
    public bool EnableVolumeFadeIn { get; set; } = true;
    public double VolumeFadeInDuration { get; set; } = 1.5;
    public double SkipIntroSeconds { get; set; }
    public double SkipOutroSeconds { get; set; }
    public bool SavePlaybackPosition { get; set; } = true;
    public bool ResumePlaybackPosition { get; set; } = true;
    public int HistoryRetentionDays { get; set; } = 30;
    public int MaxPlaybackHistoryEntries { get; set; } = 2000;
    public bool HasWindowPlacement { get; set; }
    public int WindowX { get; set; }
    public int WindowY { get; set; }
    public int WindowWidth { get; set; } = 1200;
    public int WindowHeight { get; set; } = 760;
}
