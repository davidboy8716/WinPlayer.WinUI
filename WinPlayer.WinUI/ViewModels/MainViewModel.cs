using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.IO;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Streams;
using WinPlayer.WinUI.Mvvm;
using WinPlayer.WinUI.Models;
using WinPlayer.WinUI.Services;

namespace WinPlayer.WinUI.ViewModels;

/// <summary>
/// 管理播放状态、命令、媒体/文件夹集合以及持久化流程。
/// 窗口移动和面板动画等纯界面逻辑仍由
/// <see cref="WinPlayer.WinUI.MainWindow"/> 负责。
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    public event EventHandler<UserNotificationEventArgs>? NotificationRequested;
    public event Action<PgsSubtitleImage?>? PgsSubtitleFrameChanged;

    private static readonly HashSet<string> SupportedMediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".avi", ".mp4", ".mkv", ".iso", ".wmv", ".vob", ".mpg", ".mpeg",
        ".rmvb", ".rm", ".dat", ".mov", ".m4v", ".webm", ".ts", ".mts",
        ".m2ts", ".mp3", ".m4a", ".wav", ".flac", ".ape", ".aac", ".ogg"
    };
    private static readonly HashSet<string> SupportedSubtitleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".ass", ".ssa", ".vtt", ".ttml", ".sup", ".sub"
    };
    private readonly Func<Task<IReadOnlyList<StorageFile>>> pickMediaFiles;
    private readonly Action toggleFullScreen;
    private readonly Action enterFullScreen;
    private readonly Func<Task> openSettings;
    private readonly Action persistSettings;
    private readonly DispatcherQueue dispatcherQueue;
    // 播放进度和控制层自动隐藏计时器会修改绑定状态，因此在 UI 调度器上运行。
    private readonly DispatcherTimer positionTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer chromeTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly PlaybackHistoryService playbackHistory = new();
    private readonly PlaylistPersistenceService playlistPersistence = new();
    private DateTime lastHistoryWriteUtc;
    private PlaylistItem? currentItem;
    private double pendingResumePosition;
    private bool isSkippingOutro;
    private bool forcePlayOnMediaOpened;
    private bool ignoreResumeOnce;
    private string? historySuppressedForPath;
    // 每个媒体源都有独立的版本号，旧媒体源被取消的音量渐变不能修改新媒体源的音量。
    private CancellationTokenSource? volumeFadeCancellation;
    private long mediaGeneration;
    private bool isVolumeFading;
    private bool pendingVolumeFadeIn;
    private MediaPlaybackItem? playbackItem;
    private MediaSource? activeMediaSource;
    private readonly HashSet<string> loadedExternalSubtitlePaths = new(StringComparer.OrdinalIgnoreCase);
    // TimedTextSource 不接管调用方打开的流；播放期间必须保持流有效。
    private readonly List<IRandomAccessStream> externalSubtitleStreams = new();
    private readonly Dictionary<TimedMetadataTrack, PgsSubtitleDocument> pgsSubtitleDocuments = new();
    private CancellationTokenSource? pgsDecodeCancellation;
    private PgsSubtitleDocument? activePgsSubtitleDocument;
    private int activePgsFrameIndex = -2;
    private bool hasLoggedFirstPgsFrame;
    private TimedMetadataTrack? activeSubtitleTrack;
    private string subtitleText = string.Empty;
    private string title = "私有云播放器";
    private string currentTime = "00:00";
    private string durationText = "00:00";
    private string playPauseGlyph = "▶";
    private double position;
    private double duration = 1;
    private double volume = 80;
    private double volumeBeforeMute = 80;
    private double chromeOpacity = 1;
    private Visibility emptyStateVisibility = Visibility.Visible;
    private Visibility pausedOverlayVisibility = Visibility.Collapsed;
    private bool isSeeking;
    private int loadingOperationCount;
    private bool isLoading;
    private string loadingMessage = "正在加载…";
    private PlaylistItem? selectedItem;
    private double leftSidebarWidth;
    private double rightSidebarWidth;

    public MainViewModel(Func<Task<IReadOnlyList<StorageFile>>> pickMediaFiles, Action toggleFullScreen,
        Action enterFullScreen, Action toggleFolders, Action togglePlaylist,
        DispatcherQueue dispatcherQueue, PlayerSettings settings,
        Func<Task> openSettings, Action persistSettings)
    {
        this.pickMediaFiles = pickMediaFiles;
        this.toggleFullScreen = toggleFullScreen;
        this.enterFullScreen = enterFullScreen;
        this.openSettings = openSettings;
        this.persistSettings = persistSettings;
        this.dispatcherQueue = dispatcherQueue;
        Settings = settings;
        MediaPlayer.IsVideoFrameServerEnabled = true;
        volume = settings.Volume;
        volumeBeforeMute = settings.Volume > 0 ? settings.Volume : 80;
        MediaPlayer.Volume = settings.Volume / 100d;
        MediaPlayer.MediaOpened += MediaPlayer_MediaOpened;
        MediaPlayer.MediaEnded += MediaPlayer_MediaEnded;
        MediaPlayer.MediaFailed += MediaPlayer_MediaFailed;
        MediaPlayer.PlaybackSession.PlaybackStateChanged += PlaybackSession_PlaybackStateChanged;
        positionTimer.Tick += PositionTimer_Tick;
        chromeTimer.Tick += ChromeTimer_Tick;
        positionTimer.Start();
        OpenFileCommand = new AsyncRelayCommand(OpenFileAsync);
        PlayPauseCommand = new RelayCommand(PlayPause);
        BackCommand = new RelayCommand(() => SeekBy(TimeSpan.FromSeconds(-Settings.SeekSeconds)));
        ForwardCommand = new RelayCommand(() => SeekBy(TimeSpan.FromSeconds(Settings.SeekSeconds)));
        PreviousCommand = new RelayCommand(PlayPrevious);
        NextCommand = new RelayCommand(PlayNext);
        StopPlaybackCommand = new RelayCommand(StopPlayback);
        ToggleFoldersCommand = new RelayCommand(toggleFolders);
        TogglePlaylistCommand = new RelayCommand(togglePlaylist);
        FullScreenCommand = new RelayCommand(() => { toggleFullScreen(); ShowChrome(); });
        SettingsCommand = new AsyncRelayCommand(openSettings);
        ToggleMuteCommand = new RelayCommand(ToggleMute);
        SetIntroMarkerCommand = new RelayCommand(ToggleIntroMarker);
        SetOutroMarkerCommand = new RelayCommand(ToggleOutroMarker);
        ShowChrome();
    }

    public MediaPlayer MediaPlayer { get; } = new();
    public PlayerSettings Settings { get; }
    public ICommand OpenFileCommand { get; }
    public ICommand PlayPauseCommand { get; }
    public ICommand BackCommand { get; }
    public ICommand ForwardCommand { get; }
    public ICommand PreviousCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand StopPlaybackCommand { get; }
    public ICommand ToggleFoldersCommand { get; }
    public ICommand TogglePlaylistCommand { get; }
    public ICommand FullScreenCommand { get; }
    public ICommand SettingsCommand { get; }
    public ICommand ToggleMuteCommand { get; }
    public ICommand SetIntroMarkerCommand { get; }
    public ICommand SetOutroMarkerCommand { get; }
    public string IntroMarkerText => Settings.SkipIntroSeconds > 0
        ? $"清除片头 {FormatTime(TimeSpan.FromSeconds(Settings.SkipIntroSeconds))}" : "设置片头";
    public string OutroMarkerText
    {
        get
        {
            if (Settings.SkipOutroSeconds <= 0) return "设置片尾";
            double start = Math.Max(0, Duration - Settings.SkipOutroSeconds);
            return $"清除片尾 {FormatTime(TimeSpan.FromSeconds(start))}";
        }
    }
    public string Title { get => title; private set => SetProperty(ref title, value); }
    public string CurrentTime { get => currentTime; private set => SetProperty(ref currentTime, value); }
    public string DurationText { get => durationText; private set => SetProperty(ref durationText, value); }
    public string PlayPauseGlyph { get => playPauseGlyph; private set => SetProperty(ref playPauseGlyph, value); }
    public double Duration { get => duration; private set => SetProperty(ref duration, value); }
    public double ChromeOpacity { get => chromeOpacity; private set => SetProperty(ref chromeOpacity, value); }
    public Visibility EmptyStateVisibility { get => emptyStateVisibility; private set => SetProperty(ref emptyStateVisibility, value); }
    public Visibility PausedOverlayVisibility { get => pausedOverlayVisibility; private set => SetProperty(ref pausedOverlayVisibility, value); }
    public bool IsMediaOpen => currentItem is not null && MediaPlayer.Source is not null;
    public bool IsLoading { get => isLoading; private set => SetProperty(ref isLoading, value); }
    public Visibility LoadingVisibility => IsLoading ? Visibility.Visible : Visibility.Collapsed;
    public string LoadingMessage { get => loadingMessage; private set => SetProperty(ref loadingMessage, value); }
    public ObservableRangeCollection<PlaylistItem> Playlist { get; } = new();
    public ObservableRangeCollection<string> Folders { get; } = new();
    public string SubtitleText
    {
        get => subtitleText;
        private set
        {
            if (!SetProperty(ref subtitleText, value)) return;
            OnPropertyChanged(nameof(SubtitleVisibility));
        }
    }
    public Visibility SubtitleVisibility => string.IsNullOrWhiteSpace(SubtitleText)
        ? Visibility.Collapsed : Visibility.Visible;
    public double LeftSidebarWidth { get => leftSidebarWidth; private set => SetProperty(ref leftSidebarWidth, value); }
    public double RightSidebarWidth { get => rightSidebarWidth; private set => SetProperty(ref rightSidebarWidth, value); }
    public PlaylistItem? SelectedItem
    {
        get => selectedItem;
        set
        {
            if (SetProperty(ref selectedItem, value) && value is not null) Play(value);
        }
    }

    public double Position
    {
        get => position;
        set
        {
            if (!SetProperty(ref position, value)) return;
            if (isSeeking) CurrentTime = FormatTime(TimeSpan.FromSeconds(value));
        }
    }

    public double Volume
    {
        get => volume;
        set
        {
            value = Math.Clamp(value, 0, 100);
            if (!SetProperty(ref volume, value)) return;
            if (!isVolumeFading) MediaPlayer.Volume = value / 100d;
            Settings.Volume = value;
            if (value > 0) volumeBeforeMute = value;
            OnPropertyChanged(nameof(VolumeGlyph));
        }
    }

    public void AdjustVolume(double change)
    {
        Volume += change;
        ShowChrome();
        Notify(Volume <= 0 ? "已静音" : $"音量 {Volume:0}%");
    }

    public void ShowVolumeNotification(double value) =>
        Notify(value <= 0 ? "已静音" : $"音量 {value:0}%");

    public void ShowNotification(string message, bool isError = false) =>
        Notify(message, isError);

    public string VolumeGlyph => Volume <= 0 ? "\uE74F" : Volume < 35 ? "\uE993" : Volume < 70 ? "\uE994" : "\uE767";

    private void ToggleMute()
    {
        Volume = Volume > 0 ? 0 : Math.Max(1, volumeBeforeMute);
        ShowChrome();
        Notify(Volume <= 0 ? "已静音" : $"音量 {Volume:0}%");
    }

    public void BeginSeek() => isSeeking = true;

    public async Task RestoreLastPlaybackAsync()
    {
        using IDisposable loading = BeginLoading("正在恢复播放列表…");
        // 单独恢复文件夹根节点，避免根据媒体路径反推目录而破坏用户保存的文件夹树。
        if (Playlist.Count > 0 || currentItem is not null) return;

        await playbackHistory.CleanupAsync(
            Settings.HistoryRetentionDays, Settings.MaxPlaybackHistoryEntries);
        await playbackHistory.FlushAsync();

        PlaylistPersistenceService.PersistedState persistedState = playlistPersistence.Load();
        Folders.ReplaceAll(persistedState.FolderPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase));

        var restoredItems = new List<PlaylistItem>();
        foreach (string path in persistedState.MediaPaths)
        {
            try
            {
                StorageFile file = await StorageFile.GetFileFromPathAsync(path);
                if (!IsSupportedMedia(file) || restoredItems.Any(item =>
                    string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
                restoredItems.Add(CreatePlaylistItem(file));
            }
            catch { }
        }
        Playlist.ReplaceAll(restoredItems);

        EmptyStateVisibility = Playlist.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        if (Playlist.Count > 0) RightSidebarWidth = Settings.RightSidebarWidth;

        if (!Settings.AutoPlay) return;
        string? lastPath = playbackHistory.GetMostRecentPlayablePath();
        if (string.IsNullOrWhiteSpace(lastPath)) return;

        PlaylistItem? lastItem = Playlist.FirstOrDefault(item =>
            string.Equals(item.Path, lastPath, StringComparison.OrdinalIgnoreCase));
        if (lastItem is null)
        {
            try
            {
                StorageFile file = await StorageFile.GetFileFromPathAsync(lastPath);
                if (!IsSupportedMedia(file)) return;
                lastItem = CreatePlaylistItem(file);
                Playlist.Add(lastItem);
            }
            catch { return; }
        }
        SelectedItem = lastItem;
    }

    public async Task LoadStartupArgumentsAsync(IReadOnlyList<string> arguments)
    {
        var storageItems = new List<IStorageItem>();
        string? requestedPlayPath = null;

        foreach (string rawArgument in arguments)
        {
            if (string.IsNullOrWhiteSpace(rawArgument)) continue;
            string argument = rawArgument.Trim().Trim('"');

            if (argument.StartsWith("Records:", StringComparison.OrdinalIgnoreCase))
                continue;

            bool isPlayArgument = argument.StartsWith("Play:", StringComparison.OrdinalIgnoreCase);
            bool isFolderArgument = argument.StartsWith("Folder:", StringComparison.OrdinalIgnoreCase);
            string path = isPlayArgument ? argument[5..] : isFolderArgument ? argument[7..] : argument;
            path = path.Trim().Trim('"');

            try
            {
                if (System.IO.File.Exists(path))
                {
                    StorageFile file = await StorageFile.GetFileFromPathAsync(path);
                    storageItems.Add(file);
                    if (isPlayArgument || requestedPlayPath is null) requestedPlayPath = file.Path;
                }
                else if (System.IO.Directory.Exists(path))
                {
                    storageItems.Add(await StorageFolder.GetFolderFromPathAsync(path));
                }
            }
            catch { }
        }

        if (storageItems.Count == 0) return;
        await AddDroppedItemsAsync(storageItems);

        if (string.IsNullOrWhiteSpace(requestedPlayPath)) return;
        PlaylistItem? requestedItem = Playlist.FirstOrDefault(item =>
            string.Equals(item.Path, requestedPlayPath, StringComparison.OrdinalIgnoreCase));
        if (requestedItem is null) return;

        forcePlayOnMediaOpened = true;
        SelectedItem = requestedItem;
    }

    public async Task AddDroppedItemsAsync(IReadOnlyList<IStorageItem> items)
    {
        using IDisposable loading = BeginLoading(
            items.Any(item => item is StorageFolder) ? "正在扫描文件夹…" : "正在加载媒体文件…");
        // 拖入的文件插到列表顶部并立即播放；明确拖入文件夹时则替换文件夹根节点和播放列表，
        // 从而保持两种拖放操作的既定行为。
        var files = new List<StorageFile>();
        var droppedFolders = new List<string>();
        var scannedPaths = new List<string>();

        foreach (IStorageItem item in items)
        {
            if (item is StorageFile file && IsSupportedMedia(file))
                files.Add(file);
            else if (item is StorageFolder folder)
            {
                droppedFolders.Add(folder.Path);
                MediaFolderScanResult scan = await ScanMediaFilesAsync(folder.Path);
                scannedPaths.AddRange(scan.Paths);
                if (scan.Error is not null)
                    AppLogService.Warning("MediaFolderScanFailed", "扫描拖入的媒体文件夹失败",
                        new { folder.Path }, scan.Error);
            }
        }

        foreach (string path in scannedPaths)
        {
            try { files.Add(await StorageFile.GetFileFromPathAsync(path)); }
            catch (Exception) { }
        }

        if (files.Count == 0 && droppedFolders.Count == 0)
        {
            Notify("没有可加载的媒体文件", true);
            return;
        }

        if (droppedFolders.Count == 0)
        {
            var insertedItems = new List<PlaylistItem>();
            var remainingItems = Playlist.ToList();
            foreach (StorageFile file in files
                .DistinctBy(file => file.Path, StringComparer.OrdinalIgnoreCase))
            {
                PlaylistItem? item = remainingItems.FirstOrDefault(existing =>
                    string.Equals(existing.Path, file.Path, StringComparison.OrdinalIgnoreCase));
                if (item is not null)
                    remainingItems.Remove(item);
                else
                    item = CreatePlaylistItem(file);

                insertedItems.Add(item);
            }
            Playlist.ReplaceAll(insertedItems.Concat(remainingItems));
            PlaylistItem? firstDroppedItem = insertedItems.FirstOrDefault();

            EmptyStateVisibility = Playlist.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
            if (Playlist.Count > 0) RightSidebarWidth = Settings.RightSidebarWidth;
            if (firstDroppedItem is not null)
            {
                forcePlayOnMediaOpened = true;
                SelectedItem = firstDroppedItem;
            }
            ShowChrome();
            Notify($"已添加并播放 {insertedItems.Count} 个媒体文件");
            return;
        }

        SelectedItem = null;

        // 拖入文件夹时同时替换文件夹树和播放列表。
        if (droppedFolders.Count > 0)
            Folders.ReplaceAll(droppedFolders
                .Where(folder => !string.IsNullOrWhiteSpace(folder))
                .Distinct(StringComparer.OrdinalIgnoreCase));

        Playlist.ReplaceAll(files
            .DistinctBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .OrderBy(file => file.Path, StringComparer.CurrentCultureIgnoreCase)
            .Select(CreatePlaylistItem));

        EmptyStateVisibility = Playlist.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        if (Playlist.Count > 0) RightSidebarWidth = Settings.RightSidebarWidth;
        ShowChrome();
        Notify($"已加载 {droppedFolders.Count} 个文件夹，共 {Playlist.Count} 个媒体文件");
    }

    public async Task LoadFolderAsync(string folderPath)
    {
        using IDisposable loading = BeginLoading("正在扫描文件夹中的媒体…");
        // 双击 TreeView 节点时只替换播放列表，保留文件夹树及其展开状态。
        if (string.IsNullOrWhiteSpace(folderPath)) return;
        MediaFolderScanResult scan = await ScanMediaFilesAsync(folderPath);
        if (scan.Error is not null)
        {
            AppLogService.Error("MediaFolderLoadFailed", "加载媒体文件夹失败",
                new { FolderPath = folderPath }, scan.Error);
            Notify("文件夹不存在或当前无法访问", true);
            return;
        }
        IReadOnlyList<string> paths = scan.Paths;
        var files = new List<StorageFile>(paths.Count);

        foreach (string path in paths)
        {
            try { files.Add(await StorageFile.GetFileFromPathAsync(path)); }
            catch (Exception) { }
        }

        SelectedItem = null;
        Playlist.ReplaceAll(files
            .DistinctBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .OrderBy(file => file.Path, StringComparer.CurrentCultureIgnoreCase)
            .Select(CreatePlaylistItem));

        // 此处有意不修改 Folders，以保留当前文件夹树和展开状态。
        // 用户已经主动选择了目录，即使目录中没有媒体，也不再显示首次启动引导卡片；
        // 空目录结果由顶部 InfoBar 提示即可。
        EmptyStateVisibility = Visibility.Collapsed;
        if (Playlist.Count > 0) RightSidebarWidth = Settings.RightSidebarWidth;
        ShowChrome();
        Notify(Playlist.Count == 0
            ? "已加载文件夹，当前目录没有支持的媒体文件"
            : $"已加载 {Playlist.Count} 个媒体文件");
    }

    public Task<IReadOnlyList<FolderTreeItem>> GetFolderTreeItemsAsync(
        IReadOnlyList<string> folderPaths) =>
        Task.Run<IReadOnlyList<FolderTreeItem>>(() => folderPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(CreateFolderTreeItem)
            .ToArray());

    public Task<IReadOnlyList<FolderTreeItem>> GetSubfoldersAsync(string folderPath) =>
        Task.Run<IReadOnlyList<FolderTreeItem>>(() =>
        {
            try
            {
                return Directory.EnumerateDirectories(folderPath)
                    .OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase)
                    // 只把当前层目录加入 TreeView；对子目录仅探测是否至少还有一层，
                    // 用于决定是否显示展开标识，不在此处构建孙级节点。
                    .Select(CreateFolderTreeItem)
                    .ToArray();
            }
            catch (UnauthorizedAccessException ex)
            {
                AppLogService.Warning("SubfolderEnumerationDenied", "没有权限读取子文件夹",
                    new { FolderPath = folderPath }, ex);
                return Array.Empty<FolderTreeItem>();
            }
            catch (IOException ex)
            {
                AppLogService.Warning("SubfolderEnumerationFailed", "读取子文件夹失败",
                    new { FolderPath = folderPath }, ex);
                return Array.Empty<FolderTreeItem>();
            }
        });

    private static FolderTreeItem CreateFolderTreeItem(string path)
    {
        bool hasSubfolders;
        try
        {
            hasSubfolders = Directory.EnumerateDirectories(path).Take(1).Any();
        }
        catch (UnauthorizedAccessException)
        {
            hasSubfolders = false;
        }
        catch (IOException)
        {
            hasSubfolders = false;
        }
        return new FolderTreeItem(path, hasSubfolders);
    }

    private static bool IsSupportedMedia(StorageFile file) =>
        SupportedMediaExtensions.Contains(file.FileType);

    private static Task<MediaFolderScanResult> ScanMediaFilesAsync(string rootPath) =>
        Task.Run(() =>
        {
            // 只加载当前文件夹的直属媒体文件。子文件夹由左侧 TreeView 展开后
            // 单独选择加载，避免一次选择根目录时把整个目录树加入播放列表。
            try
            {
                IReadOnlyList<string> paths = Directory.EnumerateFiles(rootPath)
                    .Where(file => SupportedMediaExtensions.Contains(Path.GetExtension(file)))
                    .OrderBy(file => file, StringComparer.CurrentCultureIgnoreCase)
                    .ToArray();
                return new MediaFolderScanResult(paths, null);
            }
            catch (UnauthorizedAccessException ex)
            {
                return new MediaFolderScanResult(Array.Empty<string>(), ex);
            }
            catch (IOException ex)
            {
                return new MediaFolderScanResult(Array.Empty<string>(), ex);
            }
        });

    private sealed record MediaFolderScanResult(IReadOnlyList<string> Paths, Exception? Error);

    public void SeekRelative(double seconds) => SeekBy(TimeSpan.FromSeconds(seconds));
    public void StopPlayback()
    {
        if (currentItem is null || MediaPlayer.Source is null)
        {
            Notify("当前没有打开的媒体文件");
            return;
        }

        // “停止”代表关闭当前媒体，而不是把播放位置重置为零后暂停。
        // 先保存有效进度，再使旧媒体源、轨道和音量任务全部失效。
        SaveCurrentPosition(true);
        Interlocked.Increment(ref mediaGeneration);
        CancelVolumeFade(false);
        pendingVolumeFadeIn = false;
        pendingResumePosition = 0;
        forcePlayOnMediaOpened = false;
        isSkippingOutro = false;
        DetachSubtitleTrack();
        ClearExternalSubtitleState();

        MediaPlayer.Pause();
        MediaPlayer.Source = null;
        playbackItem = null;
        currentItem = null;
        SelectedItem = null;

        Position = 0;
        Duration = 1;
        CurrentTime = "00:00";
        DurationText = "00:00";
        Title = "私有云播放器";
        PlayPauseGlyph = "\uE768";
        PausedOverlayVisibility = Visibility.Collapsed;
        EmptyStateVisibility = Visibility.Visible;
        SubtitleText = string.Empty;
        OnPropertyChanged(nameof(OutroMarkerText));
        OnPropertyChanged(nameof(IsMediaOpen));
        ShowChrome();
        Notify("已停止并关闭当前媒体文件");
    }
    public void RestartPlayback()
    {
        MediaPlayer.PlaybackSession.Position = TimeSpan.Zero;
        MediaPlayer.Play();
        ShowChrome();
        Notify("已重新开始播放");
    }

    public void PlayItem(PlaylistItem item)
    {
        forcePlayOnMediaOpened = true;
        if (ReferenceEquals(SelectedItem, item)) Play(item);
        else SelectedItem = item;
    }

    public async Task PlayFromBeginningAsync(PlaylistItem item)
    {
        playbackHistory.Clear(item.Path);
        item.ApplyPlaybackHistory(null);
        await playbackHistory.FlushAsync();
        // 防止 Play() 在替换同一个媒体源前把旧播放位置重新写回。
        historySuppressedForPath = item.Path;
        ignoreResumeOnce = true;
        PlayItem(item);
        Notify("已从头播放并清除该文件的续播记录");
    }

    public async Task ClearPlaybackHistoryAsync(PlaylistItem item)
    {
        if (!playbackHistory.Clear(item.Path))
        {
            Notify("该文件没有播放记录");
            return;
        }

        item.ApplyPlaybackHistory(null);
        if (ReferenceEquals(currentItem, item)) historySuppressedForPath = item.Path;
        await playbackHistory.FlushAsync();
        Notify("已清除该文件的播放记录");
    }

    public async Task ClearAllPlaybackHistoryAsync()
    {
        int count = playbackHistory.ClearAll();
        foreach (PlaylistItem item in Playlist) item.ApplyPlaybackHistory(null);
        historySuppressedForPath = currentItem?.Path;
        await playbackHistory.FlushAsync();
        Notify(count > 0 ? $"已清空 {count} 条播放记录" : "播放记录已经为空");
    }

    public async Task ApplyPlaybackHistorySettingsAsync()
    {
        await playbackHistory.CleanupAsync(
            Settings.HistoryRetentionDays, Settings.MaxPlaybackHistoryEntries);
        await playbackHistory.FlushAsync();
        RefreshPlaylistHistory();
    }
    public void SetPlaybackRate(double rate)
    {
        MediaPlayer.PlaybackSession.PlaybackRate = rate;
        Notify($"播放速度 {rate:0.##}x");
    }
    public void RefreshSkipSettings()
    {
        OnPropertyChanged(nameof(IntroMarkerText));
        OnPropertyChanged(nameof(OutroMarkerText));
    }
    private void ToggleIntroMarker()
    {
        bool clearing = Settings.SkipIntroSeconds > 0;
        Settings.SkipIntroSeconds = clearing ? 0 : Math.Max(0, Position);
        persistSettings();
        OnPropertyChanged(nameof(IntroMarkerText));
        Notify(clearing ? "已清除片头跳过位置" : $"片头结束位置：{FormatTime(TimeSpan.FromSeconds(Settings.SkipIntroSeconds))}");
    }

    private void ToggleOutroMarker()
    {
        bool clearing = Settings.SkipOutroSeconds > 0;
        Settings.SkipOutroSeconds = clearing
            ? 0 : Math.Max(0, Duration - Position);
        persistSettings();
        OnPropertyChanged(nameof(OutroMarkerText));
        Notify(clearing ? "已清除片尾跳过位置" : $"片尾提前跳过：{FormatTime(TimeSpan.FromSeconds(Settings.SkipOutroSeconds))}");
    }

    public void SetIntroMarkerPosition(double seconds, bool save)
    {
        double outroStart = Settings.SkipOutroSeconds > 0
            ? Math.Max(0, Duration - Settings.SkipOutroSeconds) : Duration;
        Settings.SkipIntroSeconds = Math.Clamp(seconds, 0, Math.Max(0, outroStart - 1));
        if (save) persistSettings();
        OnPropertyChanged(nameof(IntroMarkerText));
        if (save) Notify($"片头结束位置：{FormatTime(TimeSpan.FromSeconds(Settings.SkipIntroSeconds))}");
    }

    public void SetOutroMarkerPosition(double startSeconds, bool save)
    {
        double start = Math.Clamp(startSeconds, Math.Min(Duration, Settings.SkipIntroSeconds + 1), Duration);
        Settings.SkipOutroSeconds = Math.Max(0, Duration - start);
        if (save) persistSettings();
        OnPropertyChanged(nameof(OutroMarkerText));
        if (save) Notify($"片尾开始位置：{FormatTime(TimeSpan.FromSeconds(start))}");
    }
    public void EndSeek()
    {
        if (MediaPlayer.PlaybackSession.NaturalDuration > TimeSpan.Zero)
            MediaPlayer.PlaybackSession.Position = TimeSpan.FromSeconds(Position);
        isSeeking = false;
        ShowChrome();
    }

    public void ShowChrome()
    {
        ChromeOpacity = 1;
        chromeTimer.Stop();
        chromeTimer.Start();
    }

    private async Task OpenFileAsync()
    {
        IReadOnlyList<StorageFile> files = await pickMediaFiles();
        if (files.Count == 0) return;
        using IDisposable loading = BeginLoading("正在加载媒体文件…");
        SelectedItem = null;
        Playlist.ReplaceAll(files
            .Where(IsSupportedMedia)
            .DistinctBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .Select(CreatePlaylistItem));
        Folders.ReplaceAll(files
            .Select(file => System.IO.Path.GetDirectoryName(file.Path))
            .Where(folder => !string.IsNullOrWhiteSpace(folder))
            .Select(folder => folder!)
            .Distinct(StringComparer.OrdinalIgnoreCase));
        RightSidebarWidth = 320;
        EmptyStateVisibility = Visibility.Collapsed;
        ShowChrome();
        Notify($"已加载 {Playlist.Count} 个媒体文件");
    }

    /// <summary>显示可叠加的加载状态；最后一个加载操作结束后自动隐藏。</summary>
    public IDisposable BeginLoading(string message)
    {
        LoadingMessage = message;
        loadingOperationCount++;
        if (!IsLoading)
        {
            IsLoading = true;
            OnPropertyChanged(nameof(LoadingVisibility));
        }
        return new LoadingScope(this);
    }

    public void ClearFolders()
    {
        int count = Folders.Count;
        if (count == 0)
        {
            Notify("媒体文件夹列表已经为空");
            return;
        }

        Folders.ReplaceAll(Array.Empty<string>());
        playlistPersistence.Save(Playlist.Select(item => item.Path), Folders);
        Notify($"已清空 {count} 个媒体文件夹");
    }

    public void ClearPlaylist()
    {
        int count = Playlist.Count;
        if (count == 0)
        {
            Notify("播放列表已经为空");
            return;
        }

        // 清理列表不打断当前媒体，避免误触按钮造成播放中断。
        SelectedItem = null;
        Playlist.ReplaceAll(Array.Empty<PlaylistItem>());
        EmptyStateVisibility = currentItem is null ? Visibility.Visible : Visibility.Collapsed;
        playlistPersistence.Save(Playlist.Select(item => item.Path), Folders);
        Notify($"已清空播放列表中的 {count} 个项目");
    }

    private void EndLoading()
    {
        if (loadingOperationCount > 0) loadingOperationCount--;
        if (loadingOperationCount != 0 || !IsLoading) return;
        IsLoading = false;
        OnPropertyChanged(nameof(LoadingVisibility));
    }

    private sealed class LoadingScope(MainViewModel owner) : IDisposable
    {
        private MainViewModel? owner = owner;
        public void Dispose() => Interlocked.Exchange(ref owner, null)?.EndLoading();
    }

    private void Notify(string message, bool isError = false) =>
        NotificationRequested?.Invoke(this, new UserNotificationEventArgs(message, isError));

    private void Play(PlaylistItem item)
    {
        // 在 MediaPlayer 开始解析下一项之前，先清理当前媒体源专属状态。
        SaveCurrentPosition(true);
        historySuppressedForPath = null;
        CancelVolumeFade(false);
        Interlocked.Increment(ref mediaGeneration);
        pendingVolumeFadeIn = Settings.EnableVolumeFadeIn && Settings.VolumeFadeInDuration > 0 && Volume > 0;
        MediaPlayer.Volume = pendingVolumeFadeIn ? 0 : Volume / 100d;
        currentItem = item;
        EmptyStateVisibility = Visibility.Collapsed;
        pendingResumePosition = !ignoreResumeOnce && Settings.ResumePlaybackPosition &&
            playbackHistory.TryGet(item.Path, out PlaybackHistoryRecord record)
                ? record.Position : 0;
        ignoreResumeOnce = false;
        isSkippingOutro = false;
        Title = item.Name;
        DetachSubtitleTrack();
        ClearExternalSubtitleState();
        activeMediaSource = MediaSource.CreateFromStorageFile(item.File);
        playbackItem = new MediaPlaybackItem(activeMediaSource);
        MediaPlayer.Source = playbackItem;
        OnPropertyChanged(nameof(IsMediaOpen));
        _ = AutoLoadExternalSubtitlesAsync(item, activeMediaSource, mediaGeneration);
    }

    private PlaylistItem CreatePlaylistItem(StorageFile file)
    {
        var item = new PlaylistItem(file);
        ApplyPlaybackHistory(item);
        return item;
    }

    private void ApplyPlaybackHistory(PlaylistItem item)
    {
        item.ApplyPlaybackHistory(playbackHistory.TryGet(item.Path, out PlaybackHistoryRecord record)
            ? record : null);
    }

    private void RefreshPlaylistHistory()
    {
        foreach (PlaylistItem item in Playlist) ApplyPlaybackHistory(item);
    }

    private void StartPendingVolumeFade()
    {
        if (!pendingVolumeFadeIn)
        {
            MediaPlayer.Volume = Volume / 100d;
            return;
        }

        pendingVolumeFadeIn = false;
        CancelVolumeFade(false);
        var cancellation = new CancellationTokenSource();
        volumeFadeCancellation = cancellation;
        isVolumeFading = true;
        long generation = mediaGeneration;
        _ = RunVolumeFadeAsync(generation, cancellation);
    }

    private async Task RunVolumeFadeAsync(long generation, CancellationTokenSource cancellation)
    {
        // 使用平滑步进曲线，避免音量过渡开始和结束时出现突兀变化。
        CancellationToken token = cancellation.Token;
        try
        {
            double durationMilliseconds = Math.Max(100, Settings.VolumeFadeInDuration * 1000d);
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed.TotalMilliseconds < durationMilliseconds)
            {
                token.ThrowIfCancellationRequested();
                if (generation != mediaGeneration) return;
                double progress = Math.Clamp(stopwatch.Elapsed.TotalMilliseconds / durationMilliseconds, 0, 1);
                double eased = progress * progress * (3d - 2d * progress);
                MediaPlayer.Volume = Volume / 100d * eased;
                await Task.Delay(16, token);
            }
            if (generation == mediaGeneration) MediaPlayer.Volume = Volume / 100d;
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(volumeFadeCancellation, cancellation))
            {
                volumeFadeCancellation.Dispose();
                volumeFadeCancellation = null;
                isVolumeFading = false;
            }
        }
    }

    private void CancelVolumeFade(bool restoreTargetVolume)
    {
        CancellationTokenSource? cancellation = volumeFadeCancellation;
        volumeFadeCancellation = null;
        isVolumeFading = false;
        if (cancellation is not null)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }
        if (restoreTargetVolume) MediaPlayer.Volume = Volume / 100d;
    }

    public IReadOnlyList<MediaTrackOption> GetAudioTrackOptions()
    {
        if (playbackItem is null) return [];
        var result = new List<MediaTrackOption>();
        for (int index = 0; index < playbackItem.AudioTracks.Count; index++)
        {
            AudioTrack track = playbackItem.AudioTracks[index];
            string name = string.IsNullOrWhiteSpace(track.Label) ? $"音轨 {index + 1}" : track.Label;
            if (!string.IsNullOrWhiteSpace(track.Language)) name += $" ({track.Language})";
            result.Add(new MediaTrackOption(index, name, playbackItem.AudioTracks.SelectedIndex == index));
        }
        return result;
    }

    public IReadOnlyList<MediaTrackOption> GetSubtitleTrackOptions()
    {
        if (playbackItem is null) return [];
        var result = new List<MediaTrackOption>();
        for (int index = 0; index < playbackItem.TimedMetadataTracks.Count; index++)
        {
            TimedMetadataTrack track = playbackItem.TimedMetadataTracks[index];
            if (track.TimedMetadataKind is not (TimedMetadataKind.Caption or
                TimedMetadataKind.Subtitle or TimedMetadataKind.ImageSubtitle) &&
                !TryGetPgsDocument(track, out _)) continue;
            string name = string.IsNullOrWhiteSpace(track.Label) ? $"字幕 {result.Count + 1}" : track.Label;
            if (!string.IsNullOrWhiteSpace(track.Language)) name += $" ({track.Language})";
            result.Add(new MediaTrackOption(index, name,
                ReferenceEquals(track, activeSubtitleTrack) ||
                string.Equals(track.Id, activeSubtitleTrack?.Id, StringComparison.Ordinal)));
        }
        return result;
    }

    public async Task LoadExternalSubtitleAsync(StorageFile subtitleFile)
    {
        if (activeMediaSource is null || playbackItem is null || currentItem is null)
        {
            Notify("请先打开媒体文件", true);
            return;
        }

        long generation = mediaGeneration;
        bool added = await TryAddExternalSubtitleAsync(
            subtitleFile, activeMediaSource, generation, true);
        if (added) Notify($"正在加载外挂字幕：{subtitleFile.Name}");
    }

    private async Task AutoLoadExternalSubtitlesAsync(
        PlaylistItem mediaItem, MediaSource mediaSource, long generation)
    {
        try
        {
            StorageFolder? folder = await mediaItem.File.GetParentAsync();
            if (folder is null || generation != mediaGeneration ||
                !ReferenceEquals(mediaSource, activeMediaSource)) return;

            string mediaName = Path.GetFileNameWithoutExtension(mediaItem.Name);
            IReadOnlyList<StorageFile> files = await folder.GetFilesAsync();
            StorageFile[] candidates = files
                .Where(file => SupportedSubtitleExtensions.Contains(file.FileType) &&
                    IsMatchingSubtitleName(mediaName, Path.GetFileNameWithoutExtension(file.Name)))
                .OrderBy(file => file.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();

            bool activateFirst = true;
            foreach (StorageFile candidate in candidates)
            {
                if (generation != mediaGeneration ||
                    !ReferenceEquals(mediaSource, activeMediaSource)) return;
                bool added = await TryAddExternalSubtitleAsync(
                    candidate, mediaSource, generation, activateFirst);
                if (added) activateFirst = false;
            }

            if (candidates.Length > 0 && generation == mediaGeneration)
                Notify($"已发现 {loadedExternalSubtitlePaths.Count} 个外挂字幕");
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
        catch (Exception ex)
        {
            if (generation == mediaGeneration)
                Notify($"扫描外挂字幕失败：{ex.Message}", true);
        }
    }

    private async Task<bool> TryAddExternalSubtitleAsync(
        StorageFile subtitleFile, MediaSource mediaSource, long generation,
        bool activateWhenResolved)
    {
        AppLogService.Information("SubtitleLoadStarted", "开始加载外挂字幕", new
        {
            SubtitlePath = subtitleFile.Path,
            SubtitleType = subtitleFile.FileType,
            SubtitleSize = TryGetFileLength(subtitleFile.Path),
            MediaPath = currentItem?.Path,
            Generation = generation,
            ActivateWhenResolved = activateWhenResolved
        });
        if (generation != mediaGeneration || !ReferenceEquals(mediaSource, activeMediaSource) ||
            !SupportedSubtitleExtensions.Contains(subtitleFile.FileType) ||
            loadedExternalSubtitlePaths.Contains(subtitleFile.Path)) return false;

        try
        {
            if (subtitleFile.FileType.Equals(".srt", StringComparison.OrdinalIgnoreCase))
                return await AddSrtSubtitleAsync(
                    subtitleFile, mediaSource, generation, activateWhenResolved);
            if (subtitleFile.FileType.Equals(".sup", StringComparison.OrdinalIgnoreCase))
                return await AddPgsSubtitleAsync(
                    subtitleFile, mediaSource, generation, activateWhenResolved);

            TimedTextSource source;
            IRandomAccessStream? subtitleStream = null;
            IRandomAccessStream? indexStream = null;
            if (subtitleFile.FileType.Equals(".sub", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    subtitleStream = await subtitleFile.OpenReadAsync();
                    StorageFolder? folder = await subtitleFile.GetParentAsync();
                    if (folder is null) throw new FileNotFoundException("找不到字幕所在文件夹");
                    string indexName = Path.GetFileNameWithoutExtension(subtitleFile.Name) + ".idx";
                    StorageFile indexFile = await folder.GetFileAsync(indexName);
                    indexStream = await indexFile.OpenReadAsync();
                    source = TimedTextSource.CreateFromStreamWithIndex(subtitleStream, indexStream);
                }
                catch
                {
                    subtitleStream?.Dispose();
                    indexStream?.Dispose();
                    throw;
                }
            }
            else
            {
                subtitleStream = await subtitleFile.OpenReadAsync();
                source = TimedTextSource.CreateFromStream(subtitleStream);
            }

            string displayName = subtitleFile.Name;
            source.Resolved += (_, args) => dispatcherQueue.TryEnqueue(() =>
            {
                if (generation != mediaGeneration || playbackItem is null) return;
                if (args.Error is not null)
                {
                    loadedExternalSubtitlePaths.Remove(subtitleFile.Path);
                    AppLogService.Error("SubtitleResolveFailed", "系统字幕解析器返回错误", new
                    {
                        SubtitlePath = subtitleFile.Path,
                        SubtitleType = subtitleFile.FileType,
                        SubtitleSize = TryGetFileLength(subtitleFile.Path),
                        MediaPath = currentItem?.Path,
                        ErrorCode = args.Error.ErrorCode.ToString(),
                        ExtendedError = $"0x{args.Error.ExtendedError.HResult:X8}",
                        TrackCount = args.Tracks.Count
                    });
                    Notify($"外挂字幕加载失败：{displayName}；错误类型：" +
                        $"{args.Error.ErrorCode}，扩展错误：{args.Error.ExtendedError.HResult:X8}；" +
                        "详情已写入日志", true);
                    return;
                }

                for (int trackNumber = 0; trackNumber < args.Tracks.Count; trackNumber++)
                    args.Tracks[trackNumber].Label = args.Tracks.Count == 1
                        ? displayName : $"{displayName} ({trackNumber + 1})";

                if (!activateWhenResolved || args.Tracks.Count == 0) return;
                AppLogService.Information("SubtitleResolved", "外挂字幕解析成功", new
                {
                    SubtitlePath = subtitleFile.Path,
                    SubtitleType = subtitleFile.FileType,
                    TrackCount = args.Tracks.Count
                });
                TimedMetadataTrack resolvedTrack = args.Tracks[0];
                for (int index = 0; index < playbackItem.TimedMetadataTracks.Count; index++)
                {
                    if (!ReferenceEquals(playbackItem.TimedMetadataTracks[index], resolvedTrack)) continue;
                    SelectSubtitleTrack(index);
                    break;
                }
            });

            if (generation != mediaGeneration || !ReferenceEquals(mediaSource, activeMediaSource))
            {
                subtitleStream?.Dispose();
                indexStream?.Dispose();
                return false;
            }
            if (subtitleStream is not null) externalSubtitleStreams.Add(subtitleStream);
            if (indexStream is not null) externalSubtitleStreams.Add(indexStream);
            loadedExternalSubtitlePaths.Add(subtitleFile.Path);
            mediaSource.ExternalTimedTextSources.Add(source);
            return true;
        }
        catch (FileNotFoundException)
        {
            AppLogService.Error("SubtitleIndexMissing", "VobSub 缺少同名 IDX 索引", new
            {
                SubtitlePath = subtitleFile.Path,
                MediaPath = currentItem?.Path
            });
            Notify($"{subtitleFile.Name} 缺少同名 .idx 索引文件", true);
        }
        catch (Exception ex)
        {
            AppLogService.Error("SubtitleLoadException", "外挂字幕加载发生异常", new
            {
                SubtitlePath = subtitleFile.Path,
                SubtitleType = subtitleFile.FileType,
                SubtitleSize = TryGetFileLength(subtitleFile.Path),
                MediaPath = currentItem?.Path,
                Generation = generation
            }, ex);
            Notify($"外挂字幕加载失败：{ex.Message}", true);
        }
        return false;
    }

    private async Task<bool> AddPgsSubtitleAsync(
        StorageFile subtitleFile, MediaSource mediaSource, long generation,
        bool activateWhenAdded)
    {
        PgsSubtitleDocument document = await PgsSubtitleDocument.LoadAsync(subtitleFile);
        if (generation != mediaGeneration || !ReferenceEquals(mediaSource, activeMediaSource))
            return false;

        TimedMetadataTrack track = document.CreateTrack();
        pgsSubtitleDocuments[track] = document;
        loadedExternalSubtitlePaths.Add(subtitleFile.Path);
        mediaSource.ExternalTimedMetadataTracks.Add(track);
        AppLogService.Information("PgsParsed", "应用内 PGS 解析成功", new
        {
            SubtitlePath = subtitleFile.Path,
            SubtitleSize = TryGetFileLength(subtitleFile.Path),
            document.FrameCount,
            FirstStart = document.FirstStart,
            LastEnd = document.LastEnd
        });
        Notify($"已加载 PGS 字幕：{subtitleFile.Name}（{document.FrameCount} 帧）");
        if (activateWhenAdded)
        {
            activeSubtitleTrack = track;
            activePgsSubtitleDocument = document;
            activePgsFrameIndex = -2;
            hasLoggedFirstPgsFrame = false;
            UpdatePgsSubtitleForPosition(MediaPlayer.PlaybackSession.Position);
        }
        return true;
    }

    private async Task<bool> AddSrtSubtitleAsync(
        StorageFile subtitleFile, MediaSource mediaSource, long generation,
        bool activateWhenAdded)
    {
        string content;
        string encodingName;
        await using (Stream input = await subtitleFile.OpenStreamForReadAsync())
        using (var memory = new MemoryStream())
        {
            await input.CopyToAsync(memory);
            content = DecodeSubtitleText(memory.ToArray(), out encodingName);
        }

        var track = new TimedMetadataTrack(
            $"external-srt-{Guid.NewGuid():N}", string.Empty, TimedMetadataKind.Subtitle)
        {
            Label = subtitleFile.Name
        };

        int cueCount = 0;
        string normalized = content.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        foreach (string block in Regex.Split(normalized, @"\n\s*\n"))
        {
            string[] lines = block.Split('\n');
            int timingIndex = Array.FindIndex(lines, line => line.Contains("-->", StringComparison.Ordinal));
            if (timingIndex < 0) continue;

            string[] times = lines[timingIndex].Split("-->", 2, StringSplitOptions.TrimEntries);
            if (times.Length != 2 || !TryParseSrtTime(times[0], out TimeSpan start) ||
                !TryParseSrtTime(times[1], out TimeSpan end) || end <= start) continue;

            var cue = new TimedTextCue
            {
                Id = $"srt-{cueCount + 1}",
                StartTime = start,
                Duration = end - start
            };
            for (int lineIndex = timingIndex + 1; lineIndex < lines.Length; lineIndex++)
            {
                string line = CleanSrtText(lines[lineIndex]);
                if (line.Length > 0) cue.Lines.Add(new TimedTextLine { Text = line });
            }
            if (cue.Lines.Count == 0) continue;
            track.AddCue(cue);
            cueCount++;
        }

        if (cueCount == 0) throw new FormatException("没有找到有效的 SRT 时间轴");
        if (generation != mediaGeneration || !ReferenceEquals(mediaSource, activeMediaSource))
            return false;

        loadedExternalSubtitlePaths.Add(subtitleFile.Path);
        mediaSource.ExternalTimedMetadataTracks.Add(track);
        AppLogService.Information("SrtParsed", "应用内 SRT 解析成功", new
        {
            SubtitlePath = subtitleFile.Path,
            SubtitleSize = TryGetFileLength(subtitleFile.Path),
            CueCount = cueCount,
            DetectedEncoding = encodingName
        });
        if (activateWhenAdded)
            dispatcherQueue.TryEnqueue(() => ActivateSubtitleTrack(track, generation));
        return true;
    }

    private static string DecodeSubtitleText(byte[] bytes, out string encodingName)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            encodingName = "UTF-8 BOM";
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            encodingName = "UTF-16 LE";
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            encodingName = "UTF-16 BE";
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        try
        {
            encodingName = "UTF-8";
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            encodingName = "GB18030/GBK";
            return Encoding.GetEncoding(54936).GetString(bytes); // GB18030，兼容常见 GBK 字幕。
        }
    }

    private static long? TryGetFileLength(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : null; }
        catch { return null; }
    }

    private static bool TryParseSrtTime(string value, out TimeSpan result)
    {
        Match match = Regex.Match(value,
            @"(?<h>\d{1,3}):(?<m>\d{2}):(?<s>\d{2})[,.](?<ms>\d{1,3})");
        if (!match.Success)
        {
            result = default;
            return false;
        }

        int milliseconds = int.Parse(match.Groups["ms"].Value.PadRight(3, '0'));
        result = new TimeSpan(0, int.Parse(match.Groups["h"].Value),
            int.Parse(match.Groups["m"].Value), int.Parse(match.Groups["s"].Value), milliseconds);
        return true;
    }

    private static string CleanSrtText(string value)
    {
        string withoutTags = Regex.Replace(value, @"<[^>]+>|\{\\[^}]+\}", string.Empty);
        return WebUtility.HtmlDecode(withoutTags).Trim();
    }

    private void ActivateSubtitleTrack(TimedMetadataTrack track, long generation)
    {
        if (generation != mediaGeneration || playbackItem is null) return;
        for (int index = 0; index < playbackItem.TimedMetadataTracks.Count; index++)
        {
            if (!ReferenceEquals(playbackItem.TimedMetadataTracks[index], track)) continue;
            SelectSubtitleTrack(index);
            return;
        }
    }

    private static bool IsMatchingSubtitleName(string mediaName, string subtitleName) =>
        subtitleName.Equals(mediaName, StringComparison.OrdinalIgnoreCase) ||
        subtitleName.StartsWith(mediaName + ".", StringComparison.OrdinalIgnoreCase) ||
        subtitleName.StartsWith(mediaName + "_", StringComparison.OrdinalIgnoreCase);

    private bool TryGetPgsDocument(
        TimedMetadataTrack track, out PgsSubtitleDocument? document)
    {
        if (pgsSubtitleDocuments.TryGetValue(track, out document)) return true;
        foreach ((TimedMetadataTrack candidate, PgsSubtitleDocument value) in pgsSubtitleDocuments)
        {
            if (!string.Equals(candidate.Id, track.Id, StringComparison.Ordinal)) continue;
            document = value;
            return true;
        }
        document = null;
        return false;
    }

    private void UpdatePgsSubtitleForPosition(TimeSpan position)
    {
        PgsSubtitleDocument? document = activePgsSubtitleDocument;
        if (document is null) return;
        int frameIndex = document.FindFrameIndex(position);
        if (frameIndex == activePgsFrameIndex) return;
        activePgsFrameIndex = frameIndex;
        CancelPgsDecode();
        if (frameIndex < 0)
        {
            PgsSubtitleFrameChanged?.Invoke(null);
            return;
        }

        var cancellation = new CancellationTokenSource();
        pgsDecodeCancellation = cancellation;
        long generation = mediaGeneration;
        _ = DecodePgsFrameAsync(document, frameIndex, generation, cancellation);
    }

    private async Task DecodePgsFrameAsync(PgsSubtitleDocument document, int frameIndex,
        long generation, CancellationTokenSource cancellation)
    {
        try
        {
            PgsSubtitleImage? image = await Task.Run(
                () => document.Decode(frameIndex), cancellation.Token);
            if (cancellation.IsCancellationRequested || generation != mediaGeneration ||
                !ReferenceEquals(document, activePgsSubtitleDocument) ||
                frameIndex != activePgsFrameIndex) return;
            if (!hasLoggedFirstPgsFrame && image is not null)
            {
                hasLoggedFirstPgsFrame = true;
                int nonTransparentPixels = 0;
                byte maximumAlpha = 0;
                for (int offset = 3; offset < image.Pixels.Length; offset += 4)
                {
                    byte alpha = image.Pixels[offset];
                    if (alpha > 0) nonTransparentPixels++;
                    if (alpha > maximumAlpha) maximumAlpha = alpha;
                }
                AppLogService.Information("PgsFrameDecoded", "首个 PGS 字幕帧解码成功", new
                {
                    FrameIndex = frameIndex,
                    image.Width,
                    image.Height,
                    image.X,
                    image.Y,
                    image.CanvasWidth,
                    image.CanvasHeight,
                    NonTransparentPixels = nonTransparentPixels,
                    MaximumAlpha = maximumAlpha
                });
            }
            dispatcherQueue.TryEnqueue(() => PgsSubtitleFrameChanged?.Invoke(image));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLogService.Error("PgsFrameDecodeFailed", "PGS 字幕帧解码失败",
                new { document.Label, FrameIndex = frameIndex }, ex);
        }
    }

    private void ClearExternalSubtitleState()
    {
        loadedExternalSubtitlePaths.Clear();
        CancelPgsDecode();
        pgsSubtitleDocuments.Clear();
        activePgsSubtitleDocument = null;
        activePgsFrameIndex = -2;
        hasLoggedFirstPgsFrame = false;
        PgsSubtitleFrameChanged?.Invoke(null);
        foreach (IRandomAccessStream stream in externalSubtitleStreams) stream.Dispose();
        externalSubtitleStreams.Clear();
        activeMediaSource = null;
    }

    public void SelectAudioTrack(int index)
    {
        if (playbackItem is null || index < 0 || index >= playbackItem.AudioTracks.Count) return;
        playbackItem.AudioTracks.SelectedIndex = index;
        ShowChrome();
        AudioTrack track = playbackItem.AudioTracks[index];
        string name = string.IsNullOrWhiteSpace(track.Label) ? $"音轨 {index + 1}" : track.Label;
        Notify($"已切换到{name}");
    }

    public void SelectSubtitleTrack(int index)
    {
        // 文本字幕由 XAML 浮层绘制，图片字幕仍交由系统播放组件呈现。
        if (playbackItem is null) return;
        DetachSubtitleTrack();
        if (index < 0)
        {
            ShowChrome();
            Notify("已关闭字幕");
            return;
        }
        if (index >= playbackItem.TimedMetadataTracks.Count) return;

        TimedMetadataTrack track = playbackItem.TimedMetadataTracks[index];
        activeSubtitleTrack = track;
        if (TryGetPgsDocument(track, out PgsSubtitleDocument? pgsDocument))
        {
            activePgsSubtitleDocument = pgsDocument;
            activePgsFrameIndex = -2;
            hasLoggedFirstPgsFrame = false;
            playbackItem.TimedMetadataTracks.SetPresentationMode((uint)index,
                TimedMetadataTrackPresentationMode.Hidden);
            UpdatePgsSubtitleForPosition(MediaPlayer.PlaybackSession.Position);
        }
        else if (track.TimedMetadataKind == TimedMetadataKind.ImageSubtitle)
        {
            playbackItem.TimedMetadataTracks.SetPresentationMode((uint)index,
                TimedMetadataTrackPresentationMode.PlatformPresented);
        }
        else
        {
            track.CueEntered += SubtitleTrack_CueEntered;
            track.CueExited += SubtitleTrack_CueExited;
            playbackItem.TimedMetadataTracks.SetPresentationMode((uint)index,
                TimedMetadataTrackPresentationMode.ApplicationPresented);
        }
        ShowChrome();
        string name = string.IsNullOrWhiteSpace(track.Label) ? "所选字幕" : track.Label;
        Notify($"已启用{name}");
    }

    private void DetachSubtitleTrack()
    {
        if (playbackItem is not null)
        {
            for (uint index = 0; index < playbackItem.TimedMetadataTracks.Count; index++)
                playbackItem.TimedMetadataTracks.SetPresentationMode(index,
                    TimedMetadataTrackPresentationMode.Disabled);
        }
        if (activeSubtitleTrack is not null)
        {
            activeSubtitleTrack.CueEntered -= SubtitleTrack_CueEntered;
            activeSubtitleTrack.CueExited -= SubtitleTrack_CueExited;
            activeSubtitleTrack.CueEntered -= PgsSubtitleTrack_CueEntered;
            activeSubtitleTrack.CueExited -= PgsSubtitleTrack_CueExited;
            activeSubtitleTrack = null;
        }
        CancelPgsDecode();
        activePgsSubtitleDocument = null;
        activePgsFrameIndex = -2;
        hasLoggedFirstPgsFrame = false;
        PgsSubtitleFrameChanged?.Invoke(null);
        SubtitleText = string.Empty;
    }

    private void SubtitleTrack_CueEntered(TimedMetadataTrack sender, MediaCueEventArgs args)
    {
        if (args.Cue is not TimedTextCue cue) return;
        string text = string.Join(Environment.NewLine, cue.Lines.Select(line => line.Text));
        dispatcherQueue.TryEnqueue(() => SubtitleText = text);
    }

    private void SubtitleTrack_CueExited(TimedMetadataTrack sender, MediaCueEventArgs args) =>
        dispatcherQueue.TryEnqueue(() => SubtitleText = string.Empty);

    private async void PgsSubtitleTrack_CueEntered(TimedMetadataTrack sender, MediaCueEventArgs args)
    {
        if (args.Cue is not DataCue cue || !cue.Id.StartsWith("pgs:", StringComparison.Ordinal) ||
            !int.TryParse(cue.Id.AsSpan(4), out int frameIndex) ||
            !pgsSubtitleDocuments.TryGetValue(sender, out PgsSubtitleDocument? document)) return;

        CancelPgsDecode();
        var cancellation = new CancellationTokenSource();
        pgsDecodeCancellation = cancellation;
        long generation = mediaGeneration;
        try
        {
            PgsSubtitleImage? image = await Task.Run(() => document.Decode(frameIndex), cancellation.Token);
            if (cancellation.IsCancellationRequested || generation != mediaGeneration ||
                !ReferenceEquals(sender, activeSubtitleTrack)) return;
            dispatcherQueue.TryEnqueue(() => PgsSubtitleFrameChanged?.Invoke(image));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLogService.Error("PgsFrameDecodeFailed", "PGS 字幕帧解码失败",
                new { document.Label, FrameIndex = frameIndex }, ex);
        }
    }

    private void PgsSubtitleTrack_CueExited(TimedMetadataTrack sender, MediaCueEventArgs args)
    {
        CancelPgsDecode();
        dispatcherQueue.TryEnqueue(() => PgsSubtitleFrameChanged?.Invoke(null));
    }

    private void CancelPgsDecode()
    {
        CancellationTokenSource? cancellation = pgsDecodeCancellation;
        pgsDecodeCancellation = null;
        if (cancellation is null) return;
        cancellation.Cancel();
        cancellation.Dispose();
    }

    private void PlayPrevious()
    {
        if (Playlist.Count == 0) return;
        int index = Math.Max(0, Playlist.IndexOf(SelectedItem!) - 1);
        SelectedItem = Playlist[index];
    }

    private void PlayNext()
    {
        if (Playlist.Count == 0) return;
        int current = Playlist.IndexOf(SelectedItem!);
        SelectedItem = Playlist[(current + 1 + Playlist.Count) % Playlist.Count];
    }

    private void PlayPause()
    {
        if (currentItem is null || MediaPlayer.Source is null)
        {
            ShowChrome();
            Notify("当前没有打开的媒体文件");
            return;
        }

        if (MediaPlayer.PlaybackSession.PlaybackState == MediaPlaybackState.Playing) MediaPlayer.Pause();
        else
        {
            MediaPlayer.Play();
            StartPendingVolumeFade();
        }
        ShowChrome();
    }

    private void SeekBy(TimeSpan amount)
    {
        var session = MediaPlayer.PlaybackSession;
        var target = session.Position + amount;
        if (target < TimeSpan.Zero) target = TimeSpan.Zero;
        if (session.NaturalDuration > TimeSpan.Zero && target > session.NaturalDuration) target = session.NaturalDuration;
        session.Position = target;
        ShowChrome();
        Notify(amount.TotalSeconds < 0
            ? $"后退 {Math.Abs(amount.TotalSeconds):0} 秒"
            : $"前进 {amount.TotalSeconds:0} 秒");
    }

    private void PositionTimer_Tick(object? sender, object e)
    {
        if (isSeeking) return;
        MediaPlaybackSession session = MediaPlayer.PlaybackSession;
        Position = session.Position.TotalSeconds;
        CurrentTime = FormatTime(session.Position);
        UpdatePgsSubtitleForPosition(session.Position);

        if (Settings.SavePlaybackPosition && currentItem is not null &&
            session.PlaybackState == MediaPlaybackState.Playing &&
            DateTime.UtcNow - lastHistoryWriteUtc >= TimeSpan.FromSeconds(5))
        {
            lastHistoryWriteUtc = DateTime.UtcNow;
            SaveCurrentPosition();
        }

        double remaining = session.NaturalDuration.TotalSeconds - session.Position.TotalSeconds;
        if (!isSkippingOutro && Settings.SkipOutroSeconds > 0 &&
            session.NaturalDuration > TimeSpan.Zero && remaining <= Settings.SkipOutroSeconds)
        {
            isSkippingOutro = true;
            if (currentItem is not null)
            {
                playbackHistory.Clear(currentItem.Path);
                currentItem.ApplyPlaybackHistory(null);
                _ = playbackHistory.FlushAsync();
            }
            if (Settings.AutoPlay) PlayNext(); else MediaPlayer.Pause();
        }
    }

    private void ChromeTimer_Tick(object? sender, object e)
    {
        chromeTimer.Stop();
        if (MediaPlayer.PlaybackSession.PlaybackState == MediaPlaybackState.Playing) ChromeOpacity = 0;
    }

    private void MediaPlayer_MediaOpened(MediaPlayer sender, object args) => dispatcherQueue.TryEnqueue(() =>
    {
        // 停止操作可能发生在 MediaOpened 回调排队之后；此时不得重新启动旧媒体。
        if (currentItem is null || playbackItem is null || sender.Source is null) return;
        // 媒体回调不保证在 UI 线程触发，因此所有绑定属性更新都加入调度队列。
        var naturalDuration = sender.PlaybackSession.NaturalDuration;
        Duration = Math.Max(1, naturalDuration.TotalSeconds);
        DurationText = FormatTime(naturalDuration);
        OnPropertyChanged(nameof(OutroMarkerText));
        double target = Math.Max(Settings.SkipIntroSeconds, pendingResumePosition);
        if (sender.PlaybackSession.CanSeek && target > 0 && target < naturalDuration.TotalSeconds - 5)
            sender.PlaybackSession.Position = TimeSpan.FromSeconds(target);
        pendingResumePosition = 0;
        if (Settings.AutoPlay || forcePlayOnMediaOpened)
        {
            sender.Play();
            StartPendingVolumeFade();
        }
        forcePlayOnMediaOpened = false;
        if (Settings.AutoFullScreen) enterFullScreen();
    });
    private void MediaPlayer_MediaEnded(MediaPlayer sender, object args) => dispatcherQueue.TryEnqueue(() =>
    {
        // 已执行停止并关闭时，忽略旧媒体源排队中的结束事件。
        if (currentItem is null || playbackItem is null || sender.Source is null) return;
        playbackHistory.Clear(currentItem.Path);
        currentItem.ApplyPlaybackHistory(null);
        _ = playbackHistory.FlushAsync();
        if (Settings.AutoPlay) PlayNext();
    });
    private void MediaPlayer_MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args) => dispatcherQueue.TryEnqueue(() =>
    {
        if (currentItem is null || playbackItem is null || sender.Source is null) return;
        AppLogService.Error("MediaPlaybackFailed", "媒体播放失败", new
        {
            MediaPath = currentItem.Path,
            args.Error,
            args.ExtendedErrorCode,
            args.ErrorMessage
        });
        CancelVolumeFade(true);
        pendingVolumeFadeIn = false;
        PausedOverlayVisibility = Visibility.Collapsed;
        Title = $"播放失败：{args.ErrorMessage}";
    });
    private void PlaybackSession_PlaybackStateChanged(MediaPlaybackSession sender, object args) => dispatcherQueue.TryEnqueue(() =>
    {
        bool isPlaying = sender.PlaybackState == MediaPlaybackState.Playing;
        PlayPauseGlyph = isPlaying ? "\uE769" : "\uE768";
        PausedOverlayVisibility = sender.PlaybackState == MediaPlaybackState.Paused && currentItem is not null
            ? Visibility.Visible : Visibility.Collapsed;
    });
    private static string FormatTime(TimeSpan value) => value.TotalHours >= 1 ? value.ToString(@"hh\:mm\:ss") : value.ToString(@"mm\:ss");

    public void Dispose()
    {
        // 释放 WinRT 播放与合成资源之前，先保存用户可见状态。
        SaveCurrentPosition(true);
        playlistPersistence.Save(Playlist.Select(item => item.Path), Folders);
        DetachSubtitleTrack();
        ClearExternalSubtitleState();
        CancelVolumeFade(false);
        positionTimer.Stop();
        chromeTimer.Stop();
        playbackHistory.Dispose();
        MediaPlayer.Dispose();
    }

    private void SaveCurrentPosition(bool flushImmediately = false)
    {
        if (!Settings.SavePlaybackPosition || currentItem is null || isSkippingOutro ||
            string.Equals(historySuppressedForPath, currentItem.Path,
                StringComparison.OrdinalIgnoreCase)) return;
        MediaPlaybackSession session = MediaPlayer.PlaybackSession;
        playbackHistory.Update(currentItem.Path, session.Position.TotalSeconds,
            session.NaturalDuration.TotalSeconds, Settings.MaxPlaybackHistoryEntries);
        ApplyPlaybackHistory(currentItem);
        if (flushImmediately) _ = playbackHistory.FlushAsync();
        else _ = playbackHistory.FlushIfDueAsync(TimeSpan.FromSeconds(30));
    }
}
