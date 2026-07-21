using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace WinPlayer.WinUI.Services;

/// <summary>
/// 将运行诊断信息按 JSON Lines 格式异步写入本地日志；单个文件超过 5 MB 时轮换。
/// </summary>
public static class AppLogService
{
    private const long MaximumLogLength = 5 * 1024 * 1024;
    private static readonly SemaphoreSlim WriteLock = new(1, 1);
    public static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinPlayer.WinUI", "Logs", "winplayer.log");

    public static void Information(string eventName, string message, object? details = null) =>
        _ = WriteAsync("Information", eventName, message, details, null);

    public static void Warning(string eventName, string message,
        object? details = null, Exception? exception = null) =>
        _ = WriteAsync("Warning", eventName, message, details, exception);

    public static void Error(string eventName, string message,
        object? details = null, Exception? exception = null)
    {
        // 错误可能紧接着导致进程退出，因此等待写入完成，避免只排队却没有真正落盘。
        WriteAsync("Error", eventName, message, details, exception).GetAwaiter().GetResult();
    }

    /// <summary>读取最近的日志，并转换为适合在程序中查看的文本。</summary>
    public static async Task<string> ReadRecentAsync(int maximumEntries = 1000)
    {
        await WriteLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!File.Exists(LogPath)) return "尚未生成日志。";
            string[] lines = await File.ReadAllLinesAsync(LogPath, Encoding.UTF8).ConfigureAwait(false);
            var builder = new StringBuilder();
            foreach (string line in lines.TakeLast(Math.Max(1, maximumEntries)).Reverse())
            {
                try
                {
                    JsonNode? record = JsonNode.Parse(line);
                    string timestamp = record?["Timestamp"]?.GetValue<string>() ?? "";
                    string level = record?["Level"]?.GetValue<string>() ?? "";
                    string eventName = record?["Event"]?.GetValue<string>() ?? "";
                    string message = record?["Message"]?.GetValue<string>() ?? "";
                    builder.Append('[').Append(timestamp).Append("] [").Append(level)
                        .Append("] ").Append(eventName).Append(" - ").AppendLine(message);
                    if (record?["Details"] is JsonNode details && details.ToJsonString() != "null")
                        builder.Append("  详情：").AppendLine(details.ToJsonString());
                    string? exception = record?["Exception"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(exception))
                        builder.Append("  异常：").AppendLine(exception);
                    builder.AppendLine();
                }
                catch (JsonException)
                {
                    builder.AppendLine(line).AppendLine();
                }
            }
            return builder.Length == 0 ? "日志文件为空。" : builder.ToString();
        }
        finally
        {
            WriteLock.Release();
        }
    }

    public static string LogDirectory => Path.GetDirectoryName(LogPath)!;

    private static async Task WriteAsync(string level, string eventName, string message,
        object? details, Exception? exception)
    {
        try
        {
            var record = new
            {
                Timestamp = DateTimeOffset.Now,
                Level = level,
                Event = eventName,
                Message = message,
                Details = details,
                Exception = exception?.ToString(),
                ProcessId = Environment.ProcessId,
                OS = Environment.OSVersion.VersionString,
                Runtime = Environment.Version.ToString()
            };
            string line = JsonSerializer.Serialize(record) + Environment.NewLine;

            await WriteLock.WaitAsync().ConfigureAwait(false);
            try
            {
                string directory = Path.GetDirectoryName(LogPath)!;
                Directory.CreateDirectory(directory);
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length >= MaximumLogLength)
                {
                    string previousPath = Path.Combine(directory, "winplayer.previous.log");
                    File.Move(LogPath, previousPath, true);
                }
                await File.AppendAllTextAsync(LogPath, line, Encoding.UTF8).ConfigureAwait(false);
            }
            finally
            {
                WriteLock.Release();
            }
        }
        catch
        {
            // 日志失败不能导致播放器或异常处理流程再次失败。
        }
    }
}
