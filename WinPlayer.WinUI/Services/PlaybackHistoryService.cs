using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WinPlayer.WinUI.Models;

namespace WinPlayer.WinUI.Services;

/// <summary>
/// 使用“路径优先、轻量内容指纹兜底”的方式维护播放记录。文件移动或重命名后，
/// 通过文件大小及首、中、尾采样哈希重新关联记录，并自动把旧路径迁移为新路径。
///
/// <para>
/// 双模式隔离：普通模式读写 <c>playback-history.json</c>，隐私模式读写 <c>history.b.json</c>。
/// 因此隐私播放不会在普通记录里留下任何痕迹，普通启动的自动播放也永远不会选中隐私播放过的文件
/// —— 隔离由“数据写在哪里”这一事实保证，不需要任何一次性标记。
/// 隐私模式读取时以本区优先，未命中则回退只读普通记录，使隐私打开也能续上普通看过的进度。
/// </para>
/// </summary>
public sealed class PlaybackHistoryService : IDisposable
{
    /// <summary>版本 4 删除了版本 3 的一次性隐私标记字段（改为双文件隔离）。</summary>
    private const int CurrentFormatVersion = 4;
    private const int FingerprintSampleLength = 64 * 1024;

    private sealed class HistoryDocument
    {
        public int Version { get; set; } = CurrentFormatVersion;
        public Dictionary<string, Entry> Entries { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class Entry
    {
        public double Position { get; set; }
        public double Duration { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public long FileSize { get; set; }
        public DateTime FileLastWriteUtc { get; set; }
        public string FileName { get; set; } = string.Empty;
        public string Fingerprint { get; set; } = string.Empty;

        public Entry Clone() => new()
        {
            Position = Position,
            Duration = Duration,
            UpdatedUtc = UpdatedUtc,
            FileSize = FileSize,
            FileLastWriteUtc = FileLastWriteUtc,
            FileName = FileName,
            Fingerprint = Fingerprint
        };
    }

    private sealed record CachedIdentity(
        long FileSize, DateTime FileLastWriteUtc, string FileName, string Fingerprint);

    /// <summary>本模式的记录文件；隐私模式为 history.b.json，普通模式为 playback-history.json。</summary>
    private readonly string path;
    /// <summary>隐私模式下的只读回退来源（普通记录文件）；普通模式为 null。</summary>
    private readonly string? fallbackPath;
    private readonly object syncRoot = new();
    private readonly SemaphoreSlim saveLock = new(1, 1);
    private readonly Dictionary<string, CachedIdentity> identityCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Entry> fallbackEntries =
        new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
    private bool fallbackLoaded;
    private long changeVersion;
    private bool isDirty;
    private DateTime lastFlushUtc = DateTime.MinValue;
    private bool disposed;

    /// <summary>本模式使用的记录文件完整路径（供诊断与测试使用）。</summary>
    public string FilePath => path;

    /// <param name="privacyMode">true 表示隐私模式：读写独立文件，并只读回退普通记录。</param>
    /// <param name="dataDirectory">数据目录；为 null 时使用 <c>%LOCALAPPDATA%\WinPlayer.WinUI</c>（测试可注入临时目录）。</param>
    public PlaybackHistoryService(bool privacyMode, string? dataDirectory = null)
    {
        string directory = string.IsNullOrWhiteSpace(dataDirectory)
            ? System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinPlayer.WinUI")
            : dataDirectory;
        path = System.IO.Path.Combine(directory,
            privacyMode ? "history.b.json" : "playback-history.json");
        fallbackPath = privacyMode
            ? System.IO.Path.Combine(directory, "playback-history.json")
            : null;
        Load();
        if (privacyMode) LoadFallback();
    }

    /// <summary>快速按原路径读取，不执行文件 I/O。隐私模式未命中本区时回退只读普通记录。</summary>
    public bool TryGet(string mediaPath, out PlaybackHistoryRecord record)
    {
        lock (syncRoot)
        {
            if (entries.TryGetValue(mediaPath, out Entry? entry))
            {
                record = ToRecord(entry);
                return entry.Position > 0;
            }

            if (fallbackEntries.TryGetValue(mediaPath, out Entry? fallback))
            {
                record = ToRecord(fallback);
                return fallback.Position > 0;
            }
        }

        record = default;
        return false;
    }

    /// <summary>
    /// 异步解析记录。路径未命中时，仅在存在相同大小候选记录时计算指纹；
    /// prepareIdentity 为 true 时还会为实际播放的文件预先缓存指纹。
    /// 本区（隐私模式即隐私记录）未命中时，隐私模式回退只读普通记录。
    /// </summary>
    public async Task<PlaybackHistoryRecord?> ResolveAsync(
        string mediaPath, bool prepareIdentity = false)
    {
        PlaybackHistoryRecord? record = await ResolveOwnAsync(mediaPath, prepareIdentity)
            .ConfigureAwait(false);
        if (record is not null || fallbackPath is null) return record;

        // 只读回退：普通记录不迁移、不写回，隐私会话对它零写入。
        lock (syncRoot)
        {
            if (fallbackEntries.TryGetValue(mediaPath, out Entry? fallback) && fallback.Position > 0)
                return ToRecord(fallback);
        }
        return null;
    }

    private async Task<PlaybackHistoryRecord?> ResolveOwnAsync(
        string mediaPath, bool prepareIdentity)
    {
        if (string.IsNullOrWhiteSpace(mediaPath)) return null;

        Entry? exactEntry;
        lock (syncRoot)
        {
            entries.TryGetValue(mediaPath, out exactEntry);
            if (exactEntry is not null && !prepareIdentity)
                return exactEntry.Position > 0 ? ToRecord(exactEntry) : null;
        }

        FileInfo file;
        try
        {
            file = new FileInfo(mediaPath);
            if (!file.Exists) return exactEntry?.Position > 0 ? ToRecord(exactEntry) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLogService.Warning("PlaybackIdentityReadFailed", "读取媒体文件属性失败",
                new { MediaPath = mediaPath }, ex);
            return exactEntry?.Position > 0 ? ToRecord(exactEntry) : null;
        }

        List<KeyValuePair<string, Entry>> candidates;
        lock (syncRoot)
        {
            candidates = entries
                .Where(pair => !string.Equals(pair.Key, mediaPath,
                                   StringComparison.OrdinalIgnoreCase) &&
                               pair.Value.FileSize > 0 && pair.Value.FileSize == file.Length)
                .ToList();
        }

        if (exactEntry is null && candidates.Count == 0 && !prepareIdentity) return null;

        string fingerprint;
        try
        {
            fingerprint = await ComputeFingerprintAsync(mediaPath).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLogService.Warning("PlaybackFingerprintFailed", "生成媒体文件指纹失败",
                new { MediaPath = mediaPath }, ex);
            return exactEntry?.Position > 0 ? ToRecord(exactEntry) : null;
        }

        KeyValuePair<string, Entry>? matchedCandidate = null;
        lock (syncRoot)
        {
            identityCache[mediaPath] = new CachedIdentity(
                file.Length, file.LastWriteTimeUtc, file.Name, fingerprint);

            if (entries.TryGetValue(mediaPath, out exactEntry))
            {
                if (!string.IsNullOrWhiteSpace(exactEntry.Fingerprint) &&
                    !string.Equals(exactEntry.Fingerprint, fingerprint, StringComparison.Ordinal))
                {
                    // 路径相同但内容指纹不同，说明原文件已被替换，不能继承旧进度。
                    entries.Remove(mediaPath);
                    MarkDirtyLocked();
                    return null;
                }
                bool changed = UpdateIdentity(exactEntry, file, fingerprint);
                if (changed) MarkDirtyLocked();
                return exactEntry.Position > 0 ? ToRecord(exactEntry) : null;
            }

            KeyValuePair<string, Entry>[] matches = candidates
                .Where(candidate => IdentityMatches(candidate.Key, candidate.Value, file, fingerprint))
                .ToArray();
            if (matches.Length != 1) return null;
            matchedCandidate = matches[0];
        }

        string oldPath = matchedCandidate.Value.Key;
        // 网络路径检查可能持续数秒，绝不能在 syncRoot 内执行。
        bool oldPathExists = File.Exists(oldPath);
        lock (syncRoot)
        {
            if (entries.TryGetValue(mediaPath, out exactEntry))
                return exactEntry.Position > 0 ? ToRecord(exactEntry) : null;
            if (!entries.TryGetValue(oldPath, out Entry? liveEntry) ||
                !IdentityMatches(oldPath, liveEntry, file, fingerprint)) return null;

            // 原路径仍存在通常表示复制而不是移动：为新路径复制一条记录；
            // 原路径失效时才真正迁移，避免破坏仍可用文件的续播记录。
            Entry resolvedEntry = oldPathExists ? liveEntry.Clone() : liveEntry;
            if (!oldPathExists) entries.Remove(oldPath);
            UpdateIdentity(resolvedEntry, file, fingerprint);
            entries[mediaPath] = resolvedEntry;
            MarkDirtyLocked();

            AppLogService.Information("PlaybackHistoryRelocated", "已重新关联移动后的媒体播放记录",
                new { OldPath = oldPath, NewPath = mediaPath, resolvedEntry.FileSize });
            return resolvedEntry.Position > 0 ? ToRecord(resolvedEntry) : null;
        }
    }

    /// <summary>
    /// 返回本模式记录里最近一次播放过、且文件仍存在的媒体路径。
    /// 自动播放只在普通模式调用它：隐私记录写在另一个文件里，结构性不可能成为候选。
    /// </summary>
    public Task<string?> GetMostRecentPlayablePathAsync()
    {
        string[] candidates;
        lock (syncRoot)
        {
            candidates = entries
                .OrderByDescending(pair => pair.Value.UpdatedUtc)
                .Select(pair => pair.Key)
                .ToArray();
        }
        return Task.Run(() => candidates.FirstOrDefault(File.Exists));
    }

    /// <summary>只更新内存；调用方决定何时异步刷新到磁盘。写入始终落在本模式自己的文件里。</summary>
    public void Update(string mediaPath, double position, double duration, int maximumEntries)
    {
        if (string.IsNullOrWhiteSpace(mediaPath)) return;

        lock (syncRoot)
        {
            if (duration <= 0 || position >= duration - 5)
            {
                if (!entries.Remove(mediaPath)) return;
            }
            else
            {
                entries.TryGetValue(mediaPath, out Entry? previous);
                identityCache.TryGetValue(mediaPath, out CachedIdentity? identity);
                entries[mediaPath] = new Entry
                {
                    Position = Math.Max(0, position),
                    Duration = duration,
                    UpdatedUtc = DateTime.UtcNow,
                    FileSize = identity?.FileSize ?? previous?.FileSize ?? 0,
                    FileLastWriteUtc = identity?.FileLastWriteUtc ??
                        previous?.FileLastWriteUtc ?? DateTime.MinValue,
                    FileName = identity?.FileName ?? previous?.FileName ?? Path.GetFileName(mediaPath),
                    Fingerprint = identity?.Fingerprint ?? previous?.Fingerprint ?? string.Empty
                };
            }

            TrimToMaximumLocked(maximumEntries);
            MarkDirtyLocked();
        }
    }

    /// <summary>
    /// 清除本模式里该文件的播放位置记录。播放结束、片尾跳过等自动清理与用户主动清除都走这里；
    /// 它只影响本模式的文件，另一种模式的记录不受任何影响。
    /// </summary>
    public bool Clear(string mediaPath)
    {
        lock (syncRoot)
        {
            identityCache.Remove(mediaPath);
            if (!entries.Remove(mediaPath)) return false;
            MarkDirtyLocked();
            return true;
        }
    }

    /// <summary>清空本模式的全部记录，返回清除条数（不清另一种模式）。</summary>
    public int ClearAll()
    {
        lock (syncRoot)
        {
            int count = entries.Count;
            if (count == 0) return 0;
            entries.Clear();
            identityCache.Clear();
            MarkDirtyLocked();
            return count;
        }
    }

    /// <summary>
    /// 只清理本模式里过期及超量的记录。启动阶段不访问媒体路径，避免 NAS 离线或响应缓慢
    /// 导致恢复列表长时间等待；文件替换在真正播放时通过指纹确认。
    /// </summary>
    public Task CleanupAsync(int retentionDays, int maximumEntries) => Task.Run(() =>
    {
        DateTime cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, retentionDays));
        KeyValuePair<string, Entry>[] snapshot;
        lock (syncRoot)
        {
            snapshot = entries.Select(pair =>
                new KeyValuePair<string, Entry>(pair.Key, pair.Value.Clone())).ToArray();
        }

        KeyValuePair<string, Entry>[] invalidEntries = snapshot
            .Where(pair => pair.Value.UpdatedUtc < cutoff)
            .ToArray();

        lock (syncRoot)
        {
            bool changed = false;
            foreach ((string mediaPath, Entry snapshotEntry) in invalidEntries)
            {
                // 若扫描期间记录已被更新，则不删除新状态。
                if (!entries.TryGetValue(mediaPath, out Entry? liveEntry) ||
                    liveEntry.UpdatedUtc != snapshotEntry.UpdatedUtc) continue;
                entries.Remove(mediaPath);
                identityCache.Remove(mediaPath);
                changed = true;
            }

            if (TrimToMaximumLocked(maximumEntries)) changed = true;
            if (changed) MarkDirtyLocked();
        }
    });

    public async Task FlushIfDueAsync(TimeSpan interval)
    {
        lock (syncRoot)
        {
            if (!isDirty || DateTime.UtcNow - lastFlushUtc < interval) return;
        }
        await FlushAsync().ConfigureAwait(false);
    }

    public async Task FlushAsync()
    {
        if (disposed) return;
        await saveLock.WaitAsync().ConfigureAwait(false);
        string temporaryPath = path + ".tmp";
        try
        {
            HistoryDocument snapshot;
            long snapshotVersion;
            lock (syncRoot)
            {
                if (!isDirty) return;
                snapshot = new HistoryDocument
                {
                    Entries = entries.ToDictionary(pair => pair.Key, pair => pair.Value,
                        StringComparer.OrdinalIgnoreCase)
                };
                snapshotVersion = changeVersion;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string json = await Task.Run(() => JsonSerializer.Serialize(snapshot,
                new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);
            await File.WriteAllTextAsync(temporaryPath, json).ConfigureAwait(false);

            if (File.Exists(path))
            {
                try { File.Replace(temporaryPath, path, null, true); }
                catch (PlatformNotSupportedException) { File.Move(temporaryPath, path, true); }
                catch (IOException) { File.Move(temporaryPath, path, true); }
            }
            else
            {
                File.Move(temporaryPath, path);
            }

            lock (syncRoot)
            {
                if (changeVersion == snapshotVersion) isDirty = false;
                lastFlushUtc = DateTime.UtcNow;
            }
        }
        catch (Exception ex)
        {
            AppLogService.Error("PlaybackHistorySaveFailed", "保存播放记录失败",
                new { Path = path }, ex);
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch { }
        }
        finally
        {
            saveLock.Release();
        }
    }

    private static bool IdentityMatches(
        string oldPath, Entry entry, FileInfo file, string fingerprint)
    {
        if (!string.IsNullOrWhiteSpace(entry.Fingerprint))
            return string.Equals(entry.Fingerprint, fingerprint, StringComparison.Ordinal);

        // 旧版记录没有指纹，只允许“同名 + 同大小 + 近似相同时间戳”的唯一候选迁移。
        // 成功一次后会立即写入正式指纹，后续即使重命名也能识别。
        string oldFileName = string.IsNullOrWhiteSpace(entry.FileName)
            ? Path.GetFileName(oldPath) : entry.FileName;
        bool sameWriteTime = entry.FileLastWriteUtc == DateTime.MinValue ||
            Math.Abs((entry.FileLastWriteUtc - file.LastWriteTimeUtc).TotalSeconds) <= 2;
        return entry.FileSize == file.Length && sameWriteTime &&
               string.Equals(oldFileName, file.Name, StringComparison.OrdinalIgnoreCase);
    }

    private static bool UpdateIdentity(Entry entry, FileInfo file, string fingerprint)
    {
        bool changed = entry.FileSize != file.Length ||
                       entry.FileLastWriteUtc != file.LastWriteTimeUtc ||
                       !string.Equals(entry.FileName, file.Name, StringComparison.Ordinal) ||
                       !string.Equals(entry.Fingerprint, fingerprint, StringComparison.Ordinal);
        entry.FileSize = file.Length;
        entry.FileLastWriteUtc = file.LastWriteTimeUtc;
        entry.FileName = file.Name;
        entry.Fingerprint = fingerprint;
        return changed;
    }

    private static async Task<string> ComputeFingerprintAsync(string mediaPath)
    {
        await using var stream = new FileStream(mediaPath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, FingerprintSampleLength,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        long length = stream.Length;
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(BitConverter.GetBytes(length));

        long[] offsets =
        [
            0,
            Math.Max(0, (length - FingerprintSampleLength) / 2),
            Math.Max(0, length - FingerprintSampleLength)
        ];
        byte[] buffer = new byte[FingerprintSampleLength];
        foreach (long offset in offsets.Distinct())
        {
            stream.Position = offset;
            int totalRead = 0;
            while (totalRead < buffer.Length)
            {
                int read = await stream.ReadAsync(
                    buffer.AsMemory(totalRead, buffer.Length - totalRead)).ConfigureAwait(false);
                if (read == 0) break;
                totalRead += read;
            }
            hash.AppendData(BitConverter.GetBytes(offset));
            hash.AppendData(buffer, 0, totalRead);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private bool TrimToMaximumLocked(int maximumEntries)
    {
        int limit = Math.Clamp(maximumEntries, 100, 100000);
        int removeCount = entries.Count - limit;
        if (removeCount <= 0) return false;

        foreach (string key in entries
                     .OrderBy(pair => pair.Value.UpdatedUtc)
                     .Take(removeCount)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            entries.Remove(key);
            identityCache.Remove(key);
        }
        return true;
    }

    private static PlaybackHistoryRecord ToRecord(Entry entry) =>
        new(entry.Position, entry.Duration, entry.UpdatedUtc);

    private void MarkDirtyLocked()
    {
        isDirty = true;
        changeVersion++;
    }

    private static Dictionary<string, Entry> ReadEntries(string filePath)
    {
        if (!File.Exists(filePath)) return new(StringComparer.OrdinalIgnoreCase);
        string json = File.ReadAllText(filePath);
        using JsonDocument document = JsonDocument.Parse(json);
        // 带 Entries 的当前格式；未知字段（例如旧版的一次性隐私标记）由序列化器忽略。
        if (document.RootElement.TryGetProperty("Entries", out _))
        {
            Dictionary<string, Entry>? entries =
                JsonSerializer.Deserialize<HistoryDocument>(json)?.Entries;
            return entries is null
                ? new(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, Entry>(entries, StringComparer.OrdinalIgnoreCase);
        }
        // 兼容旧版以完整路径为根字典的 JSON 文件。
        Dictionary<string, Entry>? legacy =
            JsonSerializer.Deserialize<Dictionary<string, Entry>>(json);
        return legacy is null
            ? new(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, Entry>(legacy, StringComparer.OrdinalIgnoreCase);
    }

    private void Load()
    {
        try
        {
            entries = ReadEntries(path);
            foreach ((string mediaPath, Entry entry) in entries)
            {
                if (string.IsNullOrWhiteSpace(entry.FileName))
                    entry.FileName = Path.GetFileName(mediaPath);
                if (!string.IsNullOrWhiteSpace(entry.Fingerprint))
                    identityCache[mediaPath] = new CachedIdentity(
                        entry.FileSize, entry.FileLastWriteUtc, entry.FileName, entry.Fingerprint);
            }
        }
        catch (Exception ex)
        {
            entries = new(StringComparer.OrdinalIgnoreCase);
            AppLogService.Error("PlaybackHistoryLoadFailed", "读取播放记录失败",
                new { Path = path }, ex);
        }
    }

    /// <summary>
    /// 一次性载入普通记录作为只读回退（仅隐私模式）。失败不影响本区记录使用。
    /// </summary>
    private void LoadFallback()
    {
        if (fallbackPath is null || fallbackLoaded) return;
        fallbackLoaded = true;
        try
        {
            Dictionary<string, Entry> loaded = ReadEntries(fallbackPath);
            lock (syncRoot)
            {
                foreach ((string mediaPath, Entry entry) in loaded)
                    fallbackEntries[mediaPath] = entry;
            }
        }
        catch (Exception ex)
        {
            AppLogService.Warning("PlaybackHistoryFallbackLoadFailed",
                "读取普通模式记录失败（仅影响隐私模式续播回退）", new { Path = fallbackPath }, ex);
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        try { FlushAsync().GetAwaiter().GetResult(); }
        catch { }
        disposed = true;
        saveLock.Dispose();
    }
}
