using Microsoft.UI.Xaml;
using System;
using Windows.Storage;
using WinPlayer.WinUI.Mvvm;

namespace WinPlayer.WinUI.Models;

/// <summary>封装媒体文件及其可绑定的续播记录状态。</summary>
public sealed class PlaylistItem(StorageFile file) : ObservableObject
{
    private double resumePosition;
    private double recordedDuration;

    public StorageFile File { get; } = file;
    public string Name { get; } = file.Name;
    public string Path { get; } = file.Path;
    public double ResumePosition => resumePosition;
    public double RecordedDuration => recordedDuration;
    public bool HasPlaybackHistory => resumePosition > 0;
    public Visibility HistoryVisibility => HasPlaybackHistory
        ? Visibility.Visible : Visibility.Collapsed;
    public string HistoryText => HasPlaybackHistory
        ? $"上次播放到 {FormatTime(resumePosition)}"
        : "尚无播放记录";
    public double HistoryProgress => recordedDuration > 0
        ? Math.Clamp(resumePosition / recordedDuration * 100d, 0, 100)
        : 0;

    public void ApplyPlaybackHistory(PlaybackHistoryRecord? record)
    {
        double newPosition = record?.Position ?? 0;
        double newDuration = record?.Duration ?? 0;
        if (resumePosition == newPosition && recordedDuration == newDuration) return;

        resumePosition = newPosition;
        recordedDuration = newDuration;
        OnPropertyChanged(nameof(ResumePosition));
        OnPropertyChanged(nameof(RecordedDuration));
        OnPropertyChanged(nameof(HasPlaybackHistory));
        OnPropertyChanged(nameof(HistoryVisibility));
        OnPropertyChanged(nameof(HistoryText));
        OnPropertyChanged(nameof(HistoryProgress));
    }

    private static string FormatTime(double seconds)
    {
        TimeSpan value = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return value.TotalHours >= 1 ? value.ToString(@"hh\:mm\:ss") : value.ToString(@"mm\:ss");
    }
}
