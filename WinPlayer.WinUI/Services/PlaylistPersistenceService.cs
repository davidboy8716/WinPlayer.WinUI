using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace WinPlayer.WinUI.Services;

/// <summary>分别保存播放列表文件和文件夹树根节点，避免两组数据相互覆盖。</summary>
public sealed class PlaylistPersistenceService
{
    public sealed class PersistedState
    {
        public List<string> MediaPaths { get; set; } = [];
        public List<string> FolderPaths { get; set; } = [];
    }

    private readonly string path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinPlayer.WinUI", "playlist.json");

    public PersistedState Load()
    {
        try
        {
            if (!File.Exists(path)) return new PersistedState();
            string json = File.ReadAllText(path);
            // 旧版本直接保存字符串数组；继续兼容该格式以完成配置迁移。
            if (json.TrimStart().StartsWith("["))
                return new PersistedState
                {
                    MediaPaths = JsonSerializer.Deserialize<List<string>>(json) ?? []
                };
            return JsonSerializer.Deserialize<PersistedState>(json) ?? new PersistedState();
        }
        catch (Exception ex)
        {
            AppLogService.Error("PlaylistLoadFailed", "读取播放列表失败",
                new { Path = path }, ex);
            return new PersistedState();
        }
    }

    public void Save(IEnumerable<string> mediaPaths, IEnumerable<string> folderPaths)
    {
        // 隐私模式对播放列表与媒体文件夹列表只读：不写回文件，从而与普通模式完全隔离。
        // （唯一写入点都收口在这里，避免各处漏判。）
        if (AppMode.IsPrivacy) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var state = new PersistedState
            {
                MediaPaths = mediaPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                FolderPaths = folderPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            };
            File.WriteAllText(path, JsonSerializer.Serialize(state,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            AppLogService.Error("PlaylistSaveFailed", "保存播放列表失败",
                new { Path = path }, ex);
        }
    }
}
