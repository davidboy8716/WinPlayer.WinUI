using System;
using System.IO;
using System.Text.Json;
using WinPlayer.WinUI.Models;

namespace WinPlayer.WinUI.Services;

/// <summary>在当前用户的本地应用数据目录中读取和保存程序选项。</summary>
public sealed class SettingsService
{
    private readonly string path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinPlayer.WinUI", "settings.json");

    public PlayerSettings Load()
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<PlayerSettings>(File.ReadAllText(path)) ?? new PlayerSettings()
                : new PlayerSettings();
        }
        catch (Exception ex)
        {
            AppLogService.Error("SettingsLoadFailed", "读取设置文件失败，已使用默认设置",
                new { Path = path }, ex);
            return new PlayerSettings();
        }
    }

    public void Save(PlayerSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(settings,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            AppLogService.Error("SettingsSaveFailed", "保存设置文件失败",
                new { Path = path }, ex);
        }
    }
}
