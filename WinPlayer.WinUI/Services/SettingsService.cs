using System;
using System.IO;
using System.Text.Json;
using WinPlayer.WinUI.Models;

namespace WinPlayer.WinUI.Services;

/// <summary>
/// 在当前用户的本地应用数据目录中读取和保存程序选项。
/// 双模式隔离：普通模式用 settings.json，隐私模式用 settings.b.json；
/// 隐私模式首次运行且尚无自己的设置文件时，以普通设置为种子（内存中），
/// 避免隐私窗口因为默认值关掉 libmpv/杜比视界等关键开关导致片源无法播放。
/// </summary>
public sealed class SettingsService
{
    private readonly string path;
    private readonly string? seedPath;

    public SettingsService()
    {
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinPlayer.WinUI");
        path = Path.Combine(directory, AppMode.IsPrivacy ? "settings.b.json" : "settings.json");
        seedPath = AppMode.IsPrivacy ? Path.Combine(directory, "settings.json") : null;
    }

    /// <summary>本模式使用的设置文件完整路径（供诊断与测试使用）。</summary>
    public string FilePath => path;

    public PlayerSettings Load()
    {
        // 隐私模式：优先本模式文件；没有则以普通设置为种子（只读，不改写普通设置）。
        string source = File.Exists(path) ? path : seedPath ?? path;
        try
        {
            return File.Exists(source)
                ? JsonSerializer.Deserialize<PlayerSettings>(File.ReadAllText(source)) ?? new PlayerSettings()
                : new PlayerSettings();
        }
        catch (Exception ex)
        {
            AppLogService.Error("SettingsLoadFailed", "读取设置文件失败，已使用默认设置",
                new { Path = source }, ex);
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
