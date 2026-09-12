using System;
using System.Diagnostics;
using System.IO;

namespace WinPlayer.WinUI.Services;

/// <summary>
/// 把当前媒体交给外部播放器打开。
/// 存在的原因：杜比视界 Profile 5 使用 IPT 单层编码且带动态元数据（RPU），
/// Windows 媒体框架（含杜比视界扩展）不会处理它，画面会发绿/发紫；
/// 使用 libplacebo / MPCVR 等自绘管线的播放器（mpv、完美解码等）可以正确呈现。
/// </summary>
public static class ExternalPlayerService
{
    /// <summary>
    /// 用指定播放器打开文件；未配置或路径无效时回退到系统默认关联程序。
    /// </summary>
    public static string Open(string? mediaPath, string? playerPath)
    {
        if (string.IsNullOrWhiteSpace(mediaPath) || !File.Exists(mediaPath))
            return "当前没有可打开的媒体文件";

        try
        {
            if (!string.IsNullOrWhiteSpace(playerPath) && File.Exists(playerPath))
            {
                Process.Start(new ProcessStartInfo(playerPath, $"\"{mediaPath}\"")
                {
                    UseShellExecute = true
                });
                return $"已用 {Path.GetFileName(playerPath)} 打开当前媒体";
            }

            Process.Start(new ProcessStartInfo(mediaPath) { UseShellExecute = true });
            return "已用系统默认程序打开当前媒体（可在“选项 → HDR 与杜比视界”里指定支持杜比视界的外部播放器）";
        }
        catch (Exception ex)
        {
            AppLogService.Warning("ExternalPlayerOpenFailed", "调用外部播放器失败",
                new { MediaPath = mediaPath, PlayerPath = playerPath }, ex);
            return $"调用外部播放器失败：{ex.Message}";
        }
    }
}
