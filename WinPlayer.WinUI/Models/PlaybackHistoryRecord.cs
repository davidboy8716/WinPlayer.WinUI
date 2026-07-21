using System;

namespace WinPlayer.WinUI.Models;

/// <summary>提供给视图模型和播放列表显示使用的只读播放记录。</summary>
public readonly record struct PlaybackHistoryRecord(
    double Position,
    double Duration,
    DateTime UpdatedUtc);
