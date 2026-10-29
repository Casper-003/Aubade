using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.Windows.Storage.Pickers;
using Windows.Storage;
using Vesper.Core;

namespace Vesper.App;

public sealed class BooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is Visibility.Visible;
}

public sealed partial class MainWindow
{
    private readonly MainViewModel viewModel = new();
    private bool libraryInitialized;
    private bool pickingFolder;
    private readonly DispatcherTimer libraryFeedbackTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly ActivityStatusPresenter activityDisplay = new();
    private readonly DispatcherTimer activityRefreshTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly System.Diagnostics.Stopwatch activityClock = System.Diagnostics.Stopwatch.StartNew();
    private readonly SemaphoreSlim metadataSlots = new(2, 2);
    private readonly SemaphoreSlim artistSlots = new(2, 2);
    private readonly CancellationTokenSource metadataCancellation = new();

    private void InitializeLibraryPresentation()
    {
        DirectoryPanel.DataContext = viewModel;
        ActivityNavigationItem.DataContext = activityDisplay;
        activityRefreshTimer.Tick += (_, _) => activityDisplay.Flush(activityClock.Elapsed);
        activityRefreshTimer.Start();
        libraryFeedbackTimer.Tick += (_, _) =>
        {
            libraryFeedbackTimer.Stop();
            LibraryFeedback.Visibility = Visibility.Collapsed;
        };
        viewModel.PropertyChanged += OnLibraryPresentationChanged;
    }

    private void OnLibraryPresentationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (closed) return;
        if (e.PropertyName is nameof(MainViewModel.HasBackgroundActivity) or nameof(MainViewModel.ActivityLabel)
            or nameof(MainViewModel.ActivityTooltip) or nameof(MainViewModel.ActivityGlyph))
            activityDisplay.Request(new(viewModel.HasBackgroundActivity, viewModel.ActivityLabel, viewModel.ActivityTooltip,
                viewModel.ActivityGlyph), activityClock.Elapsed);
        if (e.PropertyName != nameof(MainViewModel.Status)) return;
        libraryFeedbackTimer.Stop();
        LibraryFeedback.Visibility = Visibility.Visible;
        libraryFeedbackTimer.Start();
    }

    private void CloseLibraryPresentation()
    {
        libraryFeedbackTimer.Stop();
        activityRefreshTimer.Stop();
        viewModel.PropertyChanged -= OnLibraryPresentationChanged;
        metadataCancellation.Cancel();
        viewModel.Dispose();
    }

    private void OnLibraryPreferenceChanged(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        viewModel.HideUnavailable = HideUnavailableSwitch.IsOn;
        SavePreferences();
    }

    private async void OnAddFolder(object sender, RoutedEventArgs e)
    {
        if (pickingFolder || !viewModel.CanManage) return;
        DirectoryFlyout.Hide();
        pickingFolder = true;
        viewModel.ChoosingDirectory = true;
        try
        {
            var picker = new FolderPicker(AppWindow.Id) { CommitButtonText = "添加文件夹" };
            var result = await picker.PickSingleFolderAsync();
            viewModel.ChoosingDirectory = false;
            pickingFolder = false;
            if (!closed && result is not null) await viewModel.AddDirectoryAsync(result.Path);
        }
        catch (Exception ex) { viewModel.ReportProblem($"无法添加文件夹：{ex.Message}"); }
        finally { pickingFolder = false; viewModel.ChoosingDirectory = false; }
    }

    private async void OnRefreshLibrary(object sender, RoutedEventArgs e) => await viewModel.RefreshAsync();

    private async void OnRemoveDirectory(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LibraryDirectoryItem directory })
            await viewModel.RemoveDirectoryAsync(directory.Path);
    }

    private async void OnFavoriteSong(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LibrarySongItem song })
            await viewModel.ToggleFavoriteAsync(song);
    }

    private async void OnQueueSong(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LibrarySongItem song }) return;
        int position = viewModel.Queue.Pending.ToList().FindIndex(entry => entry.SongId == song.Song.Id);
        if (position >= 0)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = "重复点歌",
                Content = $"这首歌已在待播列表第 {position + 1} 位，仍要再加入一次吗？",
                PrimaryButtonText = "再次加入", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        }
        viewModel.AddToQueue(song);
    }

    private async void OnSongRowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LibrarySongItem song }) await LoadSongMetadataAsync(song);
    }

    private async void OnSongRowContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (args.NewValue is LibrarySongItem song) await LoadSongMetadataAsync(song);
    }

    private async Task LoadSongMetadataAsync(LibrarySongItem song)
    {
        if (closed || song.MetadataLoaded || song.MetadataLoading || song.Song.AvailablePath is not string path) return;
        song.MetadataLoading = true;
        viewModel.BeginMetadataRead();
        bool entered = false, artistEntered = false;
        try
        {
            await metadataSlots.WaitAsync(metadataCancellation.Token);
            entered = true;
            var dimensions = await Task.Run(() => MatroskaMetadata.TryReadDimensions(path), metadataCancellation.Token);
            if (closed || !song.Song.Sources.Contains(path, StringComparer.OrdinalIgnoreCase)) return;
            if (dimensions is not null) song.Resolution = dimensions.Label;
            if (dimensions is not null) { metadataSlots.Release(); entered = false; }
            var file = await StorageFile.GetFileFromPathAsync(path);
            if (dimensions is null)
            {
                try
                {
                    var video = await file.Properties.GetVideoPropertiesAsync().AsTask(metadataCancellation.Token).WaitAsync(TimeSpan.FromSeconds(2));
                    if (closed || !song.Song.Sources.Contains(path, StringComparer.OrdinalIgnoreCase)) return;
                    if (video.Width > 0 && video.Height > 0) song.Resolution = $"{video.Height}p";
                }
                catch (Exception) { /* 不伪造 Windows 无法识别的视频画质。 */ }
            }
            if (entered) { metadataSlots.Release(); entered = false; }
            await artistSlots.WaitAsync(metadataCancellation.Token);
            artistEntered = true;
            var properties = await file.Properties.RetrievePropertiesAsync(
                ["System.Music.Artist"]).AsTask(metadataCancellation.Token).WaitAsync(TimeSpan.FromSeconds(2));
            if (closed || !song.Song.Sources.Contains(path, StringComparer.OrdinalIgnoreCase)) return;
            if (properties.TryGetValue("System.Music.Artist", out object? artist) && artist is string[] { Length: > 0 } artists)
                song.Artist = string.Join(" / ", artists);
            song.MetadataLoaded = song.HasResolution || !IsVideoFile(path);
        }
        catch (Exception) { song.MetadataLoaded = song.HasResolution; }
        finally
        {
            if (entered) metadataSlots.Release();
            if (artistEntered) artistSlots.Release();
            song.MetadataLoading = false;
            viewModel.EndMetadataRead();
            if (!closed && !song.MetadataLoaded && song.IsAvailable && !song.Song.Sources.Contains(path, StringComparer.OrdinalIgnoreCase))
                _ = LoadSongMetadataAsync(song);
        }
    }

    private static bool IsVideoFile(string path) => new[] { ".mp4", ".m4v", ".mkv", ".avi", ".wmv", ".mov" }
        .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
}
