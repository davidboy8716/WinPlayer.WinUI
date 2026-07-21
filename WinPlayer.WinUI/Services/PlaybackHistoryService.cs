using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WinPlayer.WinUI.Models;

namespace WinPlayer.WinUI.Services;

/// <summary>
/// 在内存中维护播放进度，并通过异步、串行、原子替换方式持久化统一 JSON 文件。
/// </summary>
public sealed class PlaybackHistoryService : IDisposable
{
    private sealed class Entry
    {
        public double Position { get; set; }
        public double Duration { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public long FileSize { get; set; }
        public DateTime FileLastWriteUtc { get; set; }
    }

    private readonly string path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinPlayer.WinUI", "playback-history.json");
    private readonly object syncRoot = new();
    private readonly SemaphoreSlim saveLock = new(1, 1);
    private Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
    private long changeVersion;
    private bool isDirty;
    private DateTime lastFlushUtc = DateTime.MinValue;
    private bool disposed;

    public PlaybackHistoryService() => Load();

    public bool TryGet(string mediaPath, out PlaybackHistoryRecord record)
    {
        lock (syncRoot)
        {
            if (entries.TryGetValue(mediaPath, out Entry? entry))
            {
                record = new PlaybackHistoryRecord(entry.Position, entry.Duration, entry.UpdatedUtc);
                return entry.Position > 0;
            }
        }

        record = default;
        return false;
    }

    public string? GetMostRecentPlayablePath()
    {
        lock (syncRoot)
        {
            return entries
                .OrderByDescending(pair => pair.Value.UpdatedUtc)
                .Select(pair => pair.Key)
                .FirstOrDefault();
        }
    }

    /// <summary>只更新内存；调用方决定何时异步刷新到磁盘。</summary>
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
                FileInfo file = new(mediaPath);
                entries[mediaPath] = new Entry
                {
                    Position = Math.Max(0, position),
                    Duration = duration,
                    UpdatedUtc = DateTime.UtcNow,
                    FileSize = file.Exists ? file.Length : 0,
                    FileLastWriteUtc = file.Exists ? file.LastWriteTimeUtc : DateTime.MinValue
                };
            }

            TrimToMaximumLocked(maximumEntries);
            MarkDirtyLocked();
        }
    }

    public bool Clear(string mediaPath)
    {
        lock (syncRoot)
        {
            if (!entries.Remove(mediaPath)) return false;
            MarkDirtyLocked();
            return true;
        }
    }

    public int ClearAll()
    {
        lock (syncRoot)
        {
            int count = entries.Count;
            if (count == 0) return 0;
            entries.Clear();
            MarkDirtyLocked();
            return count;
        }
    }

    /// <summary>在后台检查过期、丢失、已被替换及超量记录。</summary>
    public Task CleanupAsync(int retentionDays, int maximumEntries) => Task.Run(() =>
    {
        DateTime cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, retentionDays));
        lock (syncRoot)
        {
            bool changed = false;
            foreach ((string mediaPath, Entry entry) in entries.ToArray())
            {
                bool invalid = entry.UpdatedUtc < cutoff || !File.Exists(mediaPath);
                if (!invalid && (entry.FileSize > 0 || entry.FileLastWriteUtc != DateTime.MinValue))
                {
                    try
                    {
                        FileInfo file = new(mediaPath);
                        invalid = file.Length != entry.FileSize ||
                                  file.LastWriteTimeUtc != entry.FileLastWriteUtc;
                    }
                    catch (IOException) { invalid = true; }
                    catch (UnauthorizedAccessException) { invalid = true; }
                }

                if (invalid)
                {
                    entries.Remove(mediaPath);
                    changed = true;
                }
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
            Dictionary<string, Entry> snapshot;
            long snapshotVersion;
            lock (syncRoot)
            {
                if (!isDirty) return;
                snapshot = entries.ToDictionary(pair => pair.Key, pair => pair.Value,
                    StringComparer.OrdinalIgnoreCase);
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
        catch
        {
            // 保留原正式文件；下一次保存会再次尝试写入当前内存快照。
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch { }
        }
        finally
        {
            saveLock.Release();
        }
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
            entries.Remove(key);
        return true;
    }

    private void MarkDirtyLocked()
    {
        isDirty = true;
        changeVersion++;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(path)) return;
            Dictionary<string, Entry>? loaded =
                JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path));
            entries = loaded is null
                ? new(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, Entry>(loaded, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            entries = new(StringComparer.OrdinalIgnoreCase);
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
