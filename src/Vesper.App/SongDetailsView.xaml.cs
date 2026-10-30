using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Vesper.Core;

namespace Vesper.App;

public sealed partial class SongDetailsView : UserControl
{
    private readonly MainWindow host;
    private readonly string songId;
    private bool dialogOpen;
    private LibrarySongItem? Song => DataContext as LibrarySongItem;

    public SongDetailsView(MainWindow host, string songId)
    {
        this.host = host; this.songId = songId;
        InitializeComponent();
        RefreshSong();
    }

    private void OnDetailsSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 左侧留白随页面扩展，封面适度放大，窄窗口仍为资料与歌词保留空间。
        double leftWidth = DetailsLayout.ActualWidth / 2;
        double size = Math.Max(96, Math.Min(560, Math.Min(leftWidth - 64, DetailsLayout.ActualHeight * .68)));
        CoverPanel.Width = size;
        CoverArt.Width = CoverArt.Height = size;
        CoverActionsOffset.Y = size / 2 + 44;
    }

    private void RefreshSong()
    {
        DataContext = host.Model.FindSong(songId);
        EditButton.IsEnabled = SearchButton.IsEnabled = !host.Model.ReadOnly && Song is not null;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        host.Model.PropertyChanged += OnLibraryChanged;
        RefreshSong();
        if (Song is { } song) await host.LoadDetailsAsync(song);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => host.Model.PropertyChanged -= OnLibraryChanged;
    private void OnLibraryChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(MainViewModel.LibrarySummary)) RefreshSong(); }
    private async void OnFavorite(object sender, RoutedEventArgs e)
    { if (Song is { } song) await host.Model.ToggleFavoriteAsync(song); }
    private async void OnQueue(object sender, RoutedEventArgs e)
    { if (Song is { } song) await host.QueueSongAsync(song, sender as FrameworkElement); }

    private async void OnEdit(object sender, RoutedEventArgs e)
    {
        if (dialogOpen || Song is not { } song) return;
        dialogOpen = true;
        var title = new TextBox { Header = "歌名", Text = song.Title };
        var artist = new TextBox { Header = "艺人", Text = song.ArtistDetail };
        var album = new TextBox { Header = "专辑", Text = song.Album };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var content = new StackPanel { Spacing = 14, MinWidth = 320 };
        foreach (var field in new UIElement[] { title, artist, album,
            new TextBlock { Text = "修改保存在曲库中，原文件保持不变。", TextWrapping = TextWrapping.Wrap }, error }) content.Children.Add(field);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, RequestedTheme = ActualTheme, Title = "修改歌曲资料", Content = content,
            PrimaryButtonText = "保存", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary
        };
        title.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(title.Text);
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                args.Cancel = !await host.Model.SaveCorrectionsAsync(songId, title.Text, artist.Text, album.Text);
                if (args.Cancel) { error.Text = host.Model.Problem; error.Visibility = Visibility.Visible; }
            }
            finally { deferral.Complete(); }
        };
        try { await dialog.ShowAsync(); }
        finally { dialogOpen = false; }
        if (Song is { } current) await host.LoadDetailsAsync(current);
    }

    private async void OnSearch(object sender, RoutedEventArgs e)
    {
        if (dialogOpen || Song is not { } song) return;
        dialogOpen = true;
        using var cancellation = new CancellationTokenSource();
        string fileName = Path.GetFileNameWithoutExtension(song.Song.Sources.Concat(song.Song.KnownSources).FirstOrDefault() ?? song.Song.Title);
        var hint = SongSearchHint.From(fileName, host.Model.GetSongMetadata(songId));
        var title = new TextBox { Header = "歌名", Text = hint.Title };
        var artist = new TextBox { Header = "艺人（可选）", Text = hint.Artist };
        var search = new Button { Content = "检索", HorizontalAlignment = HorizontalAlignment.Right };
        var status = new TextBlock { Text = "核对检索词后开始检索。", TextWrapping = TextWrapping.Wrap };
        var progress = new ProgressRing { Width = 20, Height = 20, Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Left };
        var results = new ListView { MaxHeight = 240, SelectionMode = ListViewSelectionMode.Single,
            HorizontalContentAlignment = HorizontalAlignment.Stretch };
        results.ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <StackPanel Spacing="4" Padding="4,8">
                <TextBlock Text="{Binding Title}" FontWeight="SemiBold" TextWrapping="Wrap"/>
                <TextBlock Text="{Binding Artist}" TextWrapping="Wrap"/>
                <TextBlock Text="{Binding Description}" FontSize="12" TextWrapping="Wrap"/>
              </StackPanel>
            </DataTemplate>
            """);
        var content = new StackPanel { Spacing = 12, MinWidth = 360, MaxWidth = 480 };
        foreach (var element in new UIElement[] { title, artist,
            new TextBlock { Text = "文件名：" + fileName, TextWrapping = TextWrapping.Wrap, FontSize = 12 }, search, progress, status, results }) content.Children.Add(element);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, RequestedTheme = ActualTheme, Title = "检索歌曲资料", Content = new ScrollViewer { Content = content },
            PrimaryButtonText = "采用资料", CloseButtonText = "取消", IsPrimaryButtonEnabled = false
        };
        bool busy = false;
        void ClearResults(object? _, TextChangedEventArgs args)
        { results.ItemsSource = null; dialog.IsPrimaryButtonEnabled = false; }
        title.TextChanged += ClearResults; artist.TextChanged += ClearResults;
        results.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !busy && results.SelectedItem is SongMatch;
        search.Click += async (_, _) =>
        {
            if (busy || string.IsNullOrWhiteSpace(title.Text)) return;
            busy = true; search.IsEnabled = title.IsEnabled = artist.IsEnabled = false;
            dialog.IsPrimaryButtonEnabled = false; results.ItemsSource = null;
            progress.Visibility = Visibility.Visible; progress.IsActive = true;
            status.Text = "正在检索歌曲资料…";
            host.Model.BeginMetadataRead();
            try
            {
                var matches = await host.SearchSongSourcesAsync(title.Text, cancellation.Token);
                if (!string.IsNullOrWhiteSpace(artist.Text)) matches = matches.Where(match => SongNameMatching.SameArtist(match.Artist, artist.Text)).ToList();
                if (cancellation.IsCancellationRequested) return;
                results.ItemsSource = matches;
                status.Text = matches.Count == 0 ? "无匹配。可调整歌名或艺人后重试。" : "选择对应的歌曲和专辑。手动修正的字段会继续保留。";
            }
            catch (OperationCanceledException) { }
            catch (Exception) { if (!cancellation.IsCancellationRequested) status.Text = "暂时无法检索，请检查网络或稍后重试。本地资料仍可使用。"; }
            finally
            {
                busy = false; search.IsEnabled = title.IsEnabled = artist.IsEnabled = true;
                progress.IsActive = false; progress.Visibility = Visibility.Collapsed;
                host.Model.EndMetadataRead();
            }
        };
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            if (results.SelectedItem is not SongMatch match) { args.Cancel = true; return; }
            var deferral = args.GetDeferral();
            busy = true; search.IsEnabled = title.IsEnabled = artist.IsEnabled = results.IsEnabled = false;
            host.Model.BeginMetadataRead();
            try
            {
                var metadata = await host.GetSongSourceMetadataAsync(match, cancellation.Token);
                args.Cancel = !await host.Model.SaveOnlineMetadataAsync(songId, metadata);
                if (args.Cancel) status.Text = host.Model.Problem;
            }
            catch (Exception) { args.Cancel = true; status.Text = "暂时无法获取资料，请稍后重试。原资料已保留。"; }
            finally
            {
                busy = false; search.IsEnabled = title.IsEnabled = artist.IsEnabled = results.IsEnabled = true;
                host.Model.EndMetadataRead(); deferral.Complete();
            }
        };
        dialog.Closing += (_, _) => cancellation.Cancel();
        try { await dialog.ShowAsync(); }
        finally { cancellation.Cancel(); dialogOpen = false; }
        if (Song is { } current) await host.LoadDetailsAsync(current);
    }
}
