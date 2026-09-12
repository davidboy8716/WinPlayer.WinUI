using System;
using System.IO;
using System.Text;
using Windows.Media.Playback;

namespace WinPlayer.WinUI.Services;

/// <summary>
/// HDR / 杜比视界诊断工具：汇总显示器高级颜色状态、播放输出的降级原因，
/// 并尽力从媒体容器中识别杜比视界标记。
/// 目的是让“发绿发紫”“不确定有没有进 HDR”这类现象可以拿到可核对的数据，
/// 而不是只凭肉眼判断。
/// </summary>
public static class HdrDiagnosticsService
{
    /// <summary>
    /// 容器中的杜比视界标记：MP4 里是 dvcC/dvvC 配置盒与 dvhe/dvh1 编码标识，
    /// Matroska 里是 BlockAddIDType。这些标记都是 4 字节 ASCII，可直接扫描得到。
    /// </summary>
    private static readonly string[] DolbyVisionMarkers = { "dvcC", "dvvC", "dvhe", "dvh1" };

    /// <summary>头部扫描上限；短视频的配置信息必定落在此范围内，超长媒体避免整文件读取。</summary>
    private const int ProbeByteLimit = 64 * 1024 * 1024;

    /// <summary>读取窗口所在显示器的高级颜色状态（是否处于 HDR 模式、亮度能力）。</summary>
    public static string DescribeDisplay(Microsoft.UI.WindowId windowId)
    {
        try
        {
            Microsoft.Graphics.Display.DisplayInformation displayInformation =
                Microsoft.Graphics.Display.DisplayInformation.CreateForWindowId(windowId);
            Microsoft.Graphics.Display.DisplayAdvancedColorInfo info =
                displayInformation.GetAdvancedColorInfo();
            // 枚举名称这里用字符串比较，避免不同 SDK 版本成员命名差异导致的编译差异。
            string kind = info.CurrentAdvancedColorKind.ToString();
            bool hdr = string.Equals(kind, "HighDynamicRange", StringComparison.OrdinalIgnoreCase);
            return $"显示器：{kind}（{(hdr ? "HDR 已开启" : "HDR 未开启")}）" +
                $"，峰值亮度 {info.MaxLuminanceInNits:0} nits" +
                $"，全屏峰值 {info.MaxAverageFullFrameLuminanceInNits:0} nits" +
                $"，SDR 白点 {info.SdrWhiteLevelInNits:0} nits";
        }
        catch (Exception ex)
        {
            return $"显示器：读取高级颜色信息失败（{ex.GetType().Name}: {ex.Message}）";
        }
    }

    /// <summary>读取当前呈现模式与系统给出的输出降级原因（HDR 被降级为 SDR 时会在这里体现）。</summary>
    public static string DescribeSession(MediaPlayer player)
    {
        var builder = new StringBuilder();
        builder.Append(player.IsVideoFrameServerEnabled
            ? "呈现：帧服务器自绘（Win2D 8bit 表面，系统 HDR 元数据不参与）"
            : "呈现：系统直通（交由系统合成器输出）");
        try
        {
            MediaPlaybackSessionOutputDegradationPolicyState state =
                player.PlaybackSession.GetOutputDegradationPolicyState();
            builder.Append($"，输出降级原因：{state.VideoConstrictionReason}");
        }
        catch (Exception ex)
        {
            builder.Append($"，输出降级原因：读取失败（{ex.GetType().Name}）");
        }
        return builder.ToString();
    }

    /// <summary>读取视频轨道的编码信息（编码类型、Profile、分辨率）。</summary>
    public static string DescribeVideoTrack(MediaPlaybackItem? playbackItem)
    {
        try
        {
            if (playbackItem is null || playbackItem.VideoTracks.Count == 0)
                return "视频轨道：尚未解析";
            Windows.Media.MediaProperties.VideoEncodingProperties properties =
                playbackItem.VideoTracks[0].GetEncodingProperties();
            return $"视频轨道：{properties.Subtype}" +
                $"，ProfileId={properties.ProfileId}" +
                $"，{properties.Width}×{properties.Height}";
        }
        catch (Exception ex)
        {
            return $"视频轨道：读取失败（{ex.GetType().Name}）";
        }
    }

    /// <summary>
    /// 扫描媒体文件头部，查找杜比视界标记，并尽量推测 Profile。
    /// Profile 5 没有 HDR10 兼容基础层，若播放器不处理 RPU 就会出现常见的发绿/发紫；
    /// Profile 8 的基础层兼容 HDR10，多数播放器至少能按 HDR10 正确显示。
    /// 该方法会读取磁盘，请在后台线程调用。
    /// </summary>
    public static string? ProbeDolbyVision(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

        try
        {
            byte[] buffer;
            int length;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 1 << 20))
            {
                length = (int)Math.Min(stream.Length, ProbeByteLimit);
                if (length <= 0) return "杜比视界探测：文件为空";
                buffer = new byte[length];
                int read = 0;
                while (read < length)
                {
                    int count = stream.Read(buffer, read, length - read);
                    if (count <= 0) break;
                    read += count;
                }
                length = read;
            }

            if (!TryFindMarker(buffer, length, out string marker, out int markerIndex))
                return "杜比视界探测：头部未发现 dvcC/dvvC/dvhe/dvh1 标记（可能不是杜比视界内容）";

            string profileText = TryGuessProfile(buffer, length, markerIndex, out int profile)
                ? $"，推测 Profile {FormatProfile(profile)}"
                : DescribeRawRecord(buffer, length, markerIndex);
            return $"杜比视界探测：发现标记 {marker}{profileText}";
        }
        catch (Exception ex)
        {
            return $"杜比视界探测失败：{ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>把 Profile 号翻译成对排查有用的说明。</summary>
    public static string FormatProfile(int profile) => profile switch
    {
        4 => "4（MEL 双层，影视少见）",
        5 => "5（IPT 单层，无 HDR10 兼容基础层：不处理 RPU 的播放器会发绿发紫）",
        7 => "7（双层 FEL/MEL：PC 上普遍不处理增强层）",
        8 => "8（与 HDR10/HLG 兼容：至少可按基础层当 HDR10 播放）",
        _ => profile.ToString()
    };

    private static bool TryFindMarker(byte[] buffer, int length, out string marker, out int index)
    {
        foreach (string candidate in DolbyVisionMarkers)
        {
            for (int i = 0; i + candidate.Length <= length; i++)
            {
                if (buffer[i] != candidate[0]) continue;
                bool matched = true;
                for (int j = 1; j < candidate.Length; j++)
                {
                    if (buffer[i + j] == candidate[j]) continue;
                    matched = false;
                    break;
                }
                if (!matched) continue;
                marker = candidate;
                index = i;
                return true;
            }
        }
        marker = string.Empty;
        index = -1;
        return false;
    }

    /// <summary>
    /// 杜比视界配置记录的前三个字节依次是 dv_version_major、dv_version_minor，
    /// 第三个字节的高 7 位才是 dv_profile。MP4 中记录紧跟在 dvcC/dvvC 盒类型之后，
    /// Matroska 中则存在 BlockAddIDExtraData 里、与标记之间有 EBML 头，
    /// 因此这里在标记之后的一小段范围内搜索 <c>01 00</c> 开头的记录，结果只作推测。
    /// </summary>
    private static bool TryGuessProfile(byte[] buffer, int length, int markerIndex, out int profile)
    {
        profile = 0;
        int start = markerIndex + 4;
        int end = Math.Min(length - 4, start + 128);
        for (int i = start; i <= end; i++)
        {
            if (buffer[i] != 0x01 || buffer[i + 1] != 0x00) continue;
            int candidate = (buffer[i + 2] >> 1) & 0x7F;
            if (candidate is < 4 or > 9) continue;
            profile = candidate;
            return true;
        }
        return false;
    }

    /// <summary>推测失败时输出标记附近的原始字节，便于事后核对（例如用 MediaInfo 对照）。</summary>
    private static string DescribeRawRecord(byte[] buffer, int length, int markerIndex)
    {
        int start = markerIndex + 4;
        int count = Math.Max(0, Math.Min(12, length - start));
        if (count == 0) return string.Empty;
        var builder = new StringBuilder("，标记后字节");
        for (int i = 0; i < count; i++) builder.Append(' ').Append(buffer[start + i].ToString("X2"));
        return builder.ToString();
    }
}
