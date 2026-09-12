using System;
using System.Collections.Generic;

namespace WinPlayer.WinUI.Models;

/// <summary>
/// 播放器支持的媒体扩展名。拖放扫描、文件选择器和系统文件关联共用同一份列表，
/// 避免多处维护导致行为不一致。实际能否解码取决于系统安装的编解码器。
/// </summary>
public static class SupportedMedia
{
    public static readonly string[] MediaExtensions =
    {
        ".avi", ".mp4", ".mkv", ".iso", ".wmv", ".vob", ".mpg", ".mpeg",
        ".rmvb", ".rm", ".dat", ".mov", ".m4v", ".webm", ".ts", ".mts",
        ".m2ts", ".mp3", ".m4a", ".wav", ".flac", ".ape", ".aac", ".ogg"
    };

    public static readonly HashSet<string> MediaExtensionSet =
        new(MediaExtensions, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 外挂字幕扩展名。文件选择器的筛选条件与字幕加载时的校验共用同一份列表，
    /// 避免两处各写一遍导致「能选中的文件加载不了」或反之。
    /// </summary>
    public static readonly string[] SubtitleExtensions =
    {
        ".srt", ".ass", ".ssa", ".vtt", ".ttml", ".sup", ".sub"
    };

    public static readonly HashSet<string> SubtitleExtensionSet =
        new(SubtitleExtensions, StringComparer.OrdinalIgnoreCase);
}
