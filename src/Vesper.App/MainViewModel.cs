using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Vesper.Core;

namespace Vesper.App;

public sealed class MainViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private string search = "";
    private string status = "选择歌曲文件夹，开始准备你的待播列表。";
    private bool busy;
    public bool ReadOnly { get; private set; }
    public LibraryState Library { get; private set; } = new();
    public PlaybackQueue Queue { get; } = new();
    public ObservableCollection<Song> Results { get; } = [];
    public ObservableCollection<string> Directories { get; } = [];
    public LibraryStore Store { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vesper", "Prototype", "library.json"));
    public string Search { get => search; set { search = value; Filter(); Notify(); } }
    public string Status { get => status; set { status = value; Notify(); } }
    public bool Busy { get => busy; set { busy = value; Notify(); Notify(nameof(CanManage)); } }
    public bool CanManage => !Busy && !ReadOnly;
    public bool FavoritesOnly { get; private set; }
    public string PageTitle => FavoritesOnly ? "收藏" : "本地曲库";
    public string LibrarySummary => $"{Library.Songs.Count(s => s.IsAvailable)} 首可用歌曲 · 本地播放";
    public string QueueSummary => $"待播 {Queue.Pending.Count}";

    public MainViewModel() => Queue.Pending.CollectionChanged += (_, _) => Notify(nameof(QueueSummary));

    public async Task InitializeAsync()
    {
        try
        {
            Library = await Store.LoadAsync();
            foreach (string directory in Library.Directories) Directories.Add(directory);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            ReadOnly = true; Notify(nameof(CanManage));
            Status = $"无法读取曲库记录，已停止写入以保护原记录：{ex.Message}";
        }
    }

    public async Task RefreshAsync()
    {
        if (!CanManage) return;
        Busy = true;
        Status = "正在检查本地文件，首次导入会核对文件内容，请稍候…";
        try
        {
            string[] directories = Library.Directories.ToArray();
            Song[] previous = Library.Songs.ToArray();
            ScanResult scan = await Task.Run(() => LibraryScanner.ScanAsync(directories, previous));
            Library.Songs = scan.Songs;
            Filter();
            Notify(nameof(LibrarySummary));
            await Store.SaveAsync(Library);
            Status = scan.Warnings.Count == 0 ? $"曲库已更新，{LibrarySummary}。"
                : $"曲库已更新；{scan.Warnings.Count} 项读取异常。{scan.Warnings[0]}";
        }
        catch (Exception ex) { Status = $"曲库更新失败：{ex.Message}"; }
        finally { Busy = false; }
    }

    public async Task AddDirectoryAsync(string directory)
    {
        if (!CanManage) return;
        if (!Library.Directories.Contains(directory, StringComparer.OrdinalIgnoreCase))
        { Library.Directories.Add(directory); Directories.Add(directory); }
        await RefreshAsync();
    }

    public async Task RemoveDirectoryAsync(string directory)
    {
        if (!CanManage) return;
        Library.Directories.Remove(directory); Directories.Remove(directory);
        await RefreshAsync();
    }

    public void ShowFavorites(bool favorites)
    { FavoritesOnly = favorites; Notify(nameof(PageTitle)); Filter(); }

    public bool IsFavorite(Song song) => Library.Favorites.Contains(song.Id);
    public async Task ToggleFavoriteAsync(Song song)
    {
        if (ReadOnly) return;
        if (!Library.Favorites.Add(song.Id)) Library.Favorites.Remove(song.Id);
        Filter();
        try { await Store.SaveAsync(Library); }
        catch (Exception ex) { Status = $"收藏保存失败：{ex.Message}"; }
    }

    public void Filter()
    {
        Results.Clear();
        foreach (Song song in Library.Songs.Where(s => s.IsAvailable
            && (!FavoritesOnly || IsFavorite(s))
            && (string.IsNullOrWhiteSpace(Search) || s.Title.Contains(Search.Trim(), StringComparison.CurrentCultureIgnoreCase))))
            Results.Add(song);
    }

    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
