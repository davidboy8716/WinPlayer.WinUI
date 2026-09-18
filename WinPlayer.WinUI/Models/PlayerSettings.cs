namespace WinPlayer.WinUI.Models;

using System.Collections.Generic;
using System.Linq;

/// <summary>
/// 可序列化的用户首选项。旧版配置文件缺少新增属性时，属性初始化值同时作为默认值。
/// </summary>
public sealed class PlayerSettings
{
    public bool SingleInstance { get; set; } = true;
    /// <summary>
    /// 用户选择要注册系统文件关联的扩展名。默认关联全部受支持格式；
    /// 用户可在设置里勾选子集。实际是否生效还取决于是否执行过“注册关联”。
    /// </summary>
    public List<string> AssociatedExtensions { get; set; } =
        SupportedMedia.MediaExtensions.ToList();
    public bool AutoPlay { get; set; } = true;
    public bool AutoFullScreen { get; set; }
    /// <summary>单击播放区域时是否切换播放/暂停；关闭后双击仍切换全屏。</summary>
    public bool ClickToPlayPause { get; set; } = true;
    /// <summary>
    /// HDR / 杜比视界直通呈现（实验功能）：关闭帧服务器自绘，把视频画面交回系统合成器输出，
    /// 用于验证 HDR/杜比内容的色彩与 HDR 输出是否正常。
    /// 该模式下毛玻璃采样与自绘合成不生效，图片字幕改由叠加画布单独绘制。
    /// </summary>
    public bool HdrDirectPresent { get; set; }
    /// <summary>
    /// 支持杜比视界的外部播放器可执行文件路径（mpv、完美解码等）。
    /// 检测到杜比视界 Profile 5 等系统无法正确呈现的内容时，可一键交给它播放。
    /// </summary>
    public string ExternalPlayerPath { get; set; } = string.Empty;
    /// <summary>
    /// 使用 libmpv 引擎播放（方案 A：SW 渲染 API + libplacebo）。
    /// 系统媒体框架无法正确处理杜比视界 Profile 5，开启后播放整体交给 libmpv。
    /// </summary>
    public bool UseLibMpvEngine { get; set; }
    public double SeekSeconds { get; set; } = 3;
    public double LeftSidebarWidth { get; set; }
    public double RightSidebarWidth { get; set; }
    public double AnimationDuration { get; set; } = 360;
    public double AutoHideDelay { get; set; } = 3;
    public bool KeepControlBarVisible { get; set; }
    /// <summary>窗口是否始终显示在最前（总在最上，置顶）。</summary>
    public bool AlwaysOnTop { get; set; }
    /// <summary>界面主题：0=跟随系统，1=浅色，2=深色。</summary>
    public int ThemeMode { get; set; }
    public double BlurAmount { get; set; } = 30;
    public double TintOpacity { get; set; } = 0.42;
    /// <summary>
    /// 关闭本项目自己绘制的字幕层（文本浮层与外挂 .sup 图片字幕）。
    /// 注意：片源内嵌的图片字幕（内嵌 PGS/VobSub）由系统解码、本程序只负责呈现，
    /// 不受本项管辖；要关掉它请使用字幕菜单里的“关闭字幕”。
    /// 杜比视界引擎模式下字幕由 libmpv 渲染，若再叠加本项目的字幕会出现两行重合，
    /// 打开本项即可让字幕只由播放引擎负责。
    /// </summary>
    public bool HideOwnSubtitles { get; set; }
    /// <summary>
    /// libmpv 引擎：是否关闭字幕。记住用户在字幕菜单里选的“关闭字幕”，
    /// 切换媒体后仍然生效，不再回落到片源默认字幕轨。
    /// </summary>
    public bool EngineSubtitlesOff { get; set; }
    /// <summary>libmpv 引擎：上次选择的字幕轨标识（标题或语言），用于切换媒体后自动重选同一轨。</summary>
    public string EngineSubtitlePreference { get; set; } = string.Empty;
    /// <summary>libmpv 引擎：上次选择的字幕轨在字幕轨列表中的序号（从 1 开始，0 表示未记录）。</summary>
    public int EngineSubtitleOrdinal { get; set; }
    public double SubtitleBaseFontSize { get; set; } = 24;
    public double SubtitleBottomOffsetPercent { get; set; } = 17;
    /// <summary>
    /// 把本程序呈现的图片字幕（片源内嵌 PGS 由系统解码、外挂 .sup 由本程序解码）
    /// 整体上移，按播放区域高度的百分比计算。0 表示完全按片源给定的位置显示。
    /// 用途是当画面本身也带文字时，把字幕抬起来与它分开观察。
    /// </summary>
    public double SubtitleImageOffsetPercent { get; set; }
    /// <summary>
    /// 图片字幕只保留一行：0=关闭（整条提示照原样显示），1=只保留上一行，2=只保留下一行。
    /// 用于片源把一句话排成两行、或一条提示里同时放了两种语言时，只留下想读的那一行。
    /// 代价是分行的句子会少掉另一半。
    /// </summary>
    public int ImageSubtitleSingleLine { get; set; }
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
    public bool AutoShowSidebar { get; set; } = true;
}
