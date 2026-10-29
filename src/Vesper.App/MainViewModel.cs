using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using Vesper.Core;

namespace Vesper.App;

public delegate Task<ScanResult> ScanLibrary(string[] directories, Song[] previous,
    IReadOnlyDictionary<string, FileScanCache> cache, IProgress<ScanProgress> progress, CancellationToken token);

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private bool busy, initialized, choosingDirectory, disposed;
    private bool hideUnavailable = true;
    private string status = "正在读取曲库…", problem = "", activityDetail = "正在读取曲库";
    private readonly Dictionary<string, LibrarySongItem> songItems = [];
    private Dictionary<string, bool> availability = [];
    private readonly ScanLibrary scanner;
    private readonly TimeProvider clock;
    private readonly Dictionary<string, int> favoriteChanges = [];
    private int favoriteChange;
    private Task? scanTask;
    private CancellationTokenSource? scanCancellation;
    private bool scanRequested;
    private int directoryRevision, availableCount, metadataReads;
    public bool ReadOnly { get; private set; }
    public LibraryState Library { get; private set; } = new();
    public PlaybackQueue Queue { get; } = new();
    public ObservableCollection<LibrarySongItem> Results { get; } = [];
    public ObservableCollection<LibrarySongItem> FavoriteResults { get; } = [];
    public ObservableCollection<LibraryDirectoryItem> Directories { get; } = [];
    public LibraryStore Store { get; }
    public ScanStatistics? LastScanStatistics { get; private set; }
    public bool Busy
    {
        get => busy;
        private set { if (busy == value) return; busy = value; Notify(); Notify(nameof(Idle)); Notify(nameof(HasBackgroundActivity)); NotifyActivity(); NotifyState(); }
    }
    public bool HasBackgroundActivity => Busy || metadataReads > 0;
    public bool Idle => !HasBackgroundActivity;
    public string ActivityLabel => Busy ? activityDetail : metadataReads > 0 ? "读取歌曲资料" : HasProblem ? "需要注意" : "就绪";
    public string ActivityGlyph => HasProblem ? "\uE7BA" : "\uE73E";
    public string ActivityTooltip => Busy ? $"{activityDetail}。可继续添加文件夹、管理目录和点歌。"
        : metadataReads > 0 ? $"正在读取 {metadataReads} 首歌曲的画质或艺人资料，界面可继续操作。"
        : HasProblem ? Problem : LastScanStatistics is { } stats
        ? $"上次检查 {stats.Files} 个文件，用时 {stats.Elapsed.TotalSeconds:F2} 秒；完整核对 {stats.HashedFiles} 个文件，读取 {stats.HashedBytes / 1048576d:F1} MB。"
        : "没有后台活动";
    public bool ChoosingDirectory
    {
        get => choosingDirectory;
        set { choosingDirectory = value; Notify(nameof(CanManage)); }
    }
    public bool CanManage => initialized && !ReadOnly && !ChoosingDirectory && !disposed;
    public bool HasProblem => Problem.Length > 0;
    public string Problem
    {
        get => problem;
        private set { problem = value; Notify(); Notify(nameof(HasProblem)); NotifyActivity(); }
    }
    public string Status { get => status; private set { status = value; Notify(); } }
    public bool ShowHomeHint => initialized && !Busy && !ReadOnly && availableCount == 0;
    public bool HasNoDirectories => Directories.Count == 0;
    public bool HasNoResults => initialized && Results.Count == 0;
    public bool HasNoFavorites => initialized && FavoriteResults.Count == 0;
    public string FavoriteEmptyMessage => ReadOnly ? "无法读取收藏"
        : HasHiddenFavorites ? "收藏的歌曲暂不可用" : "还没有收藏歌曲";
    public string FavoriteEmptyHint => ReadOnly ? "原曲库记录已保留，请查看下方的读取异常提示。"
        : HasHiddenFavorites ? "重新连接歌曲目录并在“曲库”刷新；也可到“设置”关闭“隐藏不可用歌曲”，查看保留的收藏。"
        : "在曲库中点击歌曲旁的心形，即可在这里找到它。";
    private bool HasHiddenFavorites => HideUnavailable && Library.Songs.Any(song => Library.Favorites.Contains(song.Id));
    public string DirectorySummary => $"{Directories.Count} 个目录";
    public string LibrarySummary => $"{availableCount} 首可用 · {Results.Count} 首显示";
    public string EmptyMessage => Busy ? "正在收录歌曲…" : ReadOnly ? "无法读取曲库，请查看下方提示。"
        : Library.Directories.Count == 0 ? "尚未导入歌曲" : "没有可显示的歌曲";
    public string EmptyHint => ReadOnly ? "原曲库记录已保留，读取异常见下方提示。"
        : Busy ? "可以继续添加其他歌曲文件夹，后台会自动接着处理。"
        : Library.Directories.Count == 0 ? "点击右上角的文件夹加号，选择本地歌曲或 MV 所在目录。"
        : "用右上角的刷新图标重新检查文件；也可到“设置”关闭“隐藏不可用歌曲”，查看保留的记录。";
    public bool HideUnavailable
    {
        get => hideUnavailable;
        set { if (hideUnavailable == value) return; hideUnavailable = value; Notify(); Filter(); }
    }

    public MainViewModel(string? storePath = null, ScanLibrary? scanner = null, TimeProvider? timeProvider = null)
    {
        Store = new(storePath ?? Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData), "Vesper", "Prototype", "library.json"));
        this.scanner = scanner ?? ((directories, previous, cache, progress, token) =>
            LibraryScanner.ScanAsync(directories, previous, token, cache, progress));
        clock = timeProvider ?? TimeProvider.System;
    }

    public async Task InitializeAsync()
    {
        if (initialized || disposed) return;
        Busy = true;
        try
        {
            Library = await Store.LoadAsync();
            if (Library.Directories is null || Library.Songs is null || Library.Favorites is null
                || Library.Directories.Any(string.IsNullOrWhiteSpace)
                || Library.Songs.Any(s => s is null || string.IsNullOrWhiteSpace(s.Id) || s.Title is null || s.Sources is null)
                || Library.Songs.Select(s => s.Id).Distinct().Count() != Library.Songs.Count)
                throw new InvalidDataException("曲库记录结构不完整。");
            Library.ScanCache ??= new(StringComparer.OrdinalIgnoreCase);
            Library.FavoriteAddedAt ??= [];
            Song[] loaded = Library.Songs.ToArray();
            availability = await Task.Run(() => GetAvailability(loaded));
            initialized = true;
            RebuildDirectories(); Filter();
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Library = new(); ReadOnly = true; initialized = true; Busy = false;
            Problem = $"无法读取曲库记录，已停止写入以保护原记录：{ex.Message}";
            Status = "曲库未加载"; NotifyState();
        }
    }

    public Task RefreshAsync()
    {
        if (!initialized || ReadOnly || disposed) return Task.CompletedTask;
        scanRequested = true;
        if (scanTask is null || scanTask.IsCompleted) scanTask = RunScanLoopAsync();
        return scanTask;
    }

    private async Task RunScanLoopAsync()
    {
        activityDetail = "正在检查曲库";
        Busy = true;
        await Task.Yield();
        try
        {
            while (scanRequested && !disposed)
            {
                scanRequested = false;
                int revision = directoryRevision;
                var passTimer = Stopwatch.StartNew();
                string[] directories = Library.Directories.ToArray();
                Song[] previous = Library.Songs.ToArray();
                var cache = new Dictionary<string, FileScanCache>(Library.ScanCache, StringComparer.OrdinalIgnoreCase);
                using var cancellation = new CancellationTokenSource();
                scanCancellation = cancellation;
                Problem = "";
                activityDetail = "正在检查曲库"; NotifyActivity();
                var progress = new Progress<ScanProgress>(report =>
                {
                    if (disposed || !Busy || revision != directoryRevision) return;
                    activityDetail = report.Total > 0 ? $"{report.Phase} · {report.Completed}/{report.Total}"
                        : $"{report.Phase} · {report.Completed}";
                    NotifyActivity();
                });
                try
                {
                    ScanResult scan = await Task.Run(() => scanner(directories, previous, cache, progress, cancellation.Token));
                    var available = await Task.Run(() => GetAvailability(scan.Songs));
                    if (disposed) break;
                    Library.ScanCache = scan.Cache;
                    if (revision != directoryRevision) { scanRequested = true; continue; }
                    Library.Songs = scan.Songs;
                    RemapFavorites(scan.Aliases);
                    Queue.RemapSongIds(scan.Aliases);
                    availability = available;
                    LastScanStatistics = scan.Statistics;
                    Filter(); RebuildDirectories();
                    activityDetail = "正在保存曲库"; NotifyActivity();
                    await Store.SaveAsync(Library);
                    LastScanStatistics = scan.Statistics with { Elapsed = passTimer.Elapsed };
                    if (revision == directoryRevision)
                    {
                        Status = $"扫描完成 · {availableCount} 首可用歌曲";
                        if (scan.Warnings.Count > 0) Problem = $"{scan.Warnings.Count} 项读取异常。{scan.Warnings[0]}";
                    }
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                { if (!disposed) scanRequested = true; }
                catch (Exception ex) { ReportProblem($"曲库更新失败：{ex.Message}"); }
                finally { scanCancellation = null; }
            }
        }
        finally { Busy = false; scanTask = null; NotifyActivity(); }
    }

    public Task AddDirectoryAsync(string directory) => AddDirectoriesAsync([directory]);

    public async Task AddDirectoriesAsync(IEnumerable<string> directories)
    {
        if (!CanManage) return;
        bool changed = false;
        var existing = new HashSet<string>(Library.Directories.Select(Normalize), StringComparer.OrdinalIgnoreCase);
        foreach (string directory in directories)
        {
            string normalized = Normalize(directory);
            if (existing.Add(normalized)) { Library.Directories.Add(normalized); changed = true; }
        }
        if (!changed) { Status = "这些文件夹已经在曲库中。"; return; }
        DirectoriesChanged();
        await PersistAsync();
        await RefreshAsync();
    }

    public async Task RemoveDirectoryAsync(string directory)
    {
        if (!CanManage || !Library.Directories.Remove(directory)) return;
        Library.Songs = Library.Songs.Select(song => song with
        {
            KnownSources = song.Sources.Concat(song.KnownSources ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Sources = song.Sources.Where(path => Library.Directories.Any(root => IsWithin(path, root))).ToList()
        }).ToList();
        availability = Library.Songs.ToDictionary(song => song.Id,
            song => song.Sources.Count > 0 && availability.GetValueOrDefault(song.Id));
        Filter(); DirectoriesChanged();
        await PersistAsync();
        await RefreshAsync();
    }

    private void DirectoriesChanged()
    {
        directoryRevision++; scanRequested = true;
        scanCancellation?.Cancel(); RebuildDirectories();
    }

    private async Task PersistAsync()
    {
        try { await Store.SaveAsync(Library); }
        catch (Exception ex) { ReportProblem($"曲库保存失败：{ex.Message}"); }
    }

    public async Task ToggleFavoriteAsync(LibrarySongItem item)
    {
        if (ReadOnly || !initialized || disposed) return;
        string id = item.Song.Id;
        int revision = favoriteChanges[id] = ++favoriteChange;
        bool wasFavorite = Library.Favorites.Contains(id);
        DateTimeOffset? previousTime = Library.FavoriteAddedAt.TryGetValue(id, out var savedTime) ? savedTime : null;
        if (wasFavorite) { Library.Favorites.Remove(id); Library.FavoriteAddedAt.Remove(id); }
        else
        {
            Library.Favorites.Add(id);
            DateTimeOffset now = clock.GetUtcNow();
            DateTimeOffset latest = Library.FavoriteAddedAt.Values.DefaultIfEmpty(DateTimeOffset.MinValue).Max();
            // 同一时钟刻度内连续收藏、或系统时间回拨时，也保持操作先后顺序。
            Library.FavoriteAddedAt[id] = now > latest ? now
                : latest < DateTimeOffset.MaxValue ? latest.AddTicks(1) : latest;
        }
        item.IsFavorite = !wasFavorite;
        UpdateFavorites();
        try { await Store.SaveAsync(Library); }
        catch (Exception ex)
        {
            // 较早的保存失败不能撤销随后发生的点击或歌曲合并。
            if (favoriteChanges.GetValueOrDefault(id) == revision)
            {
                if (wasFavorite) Library.Favorites.Add(id); else Library.Favorites.Remove(id);
                if (previousTime is { } time) Library.FavoriteAddedAt[id] = time; else Library.FavoriteAddedAt.Remove(id);
                item.IsFavorite = wasFavorite;
                UpdateFavorites();
            }
            ReportProblem($"收藏保存失败：{ex.Message}");
        }
    }

    private void RemapFavorites(IReadOnlyDictionary<string, string> aliases)
    {
        foreach (var alias in aliases)
        {
            favoriteChanges[alias.Key] = ++favoriteChange;
            favoriteChanges[alias.Value] = ++favoriteChange;
            if (Library.Favorites.Remove(alias.Key))
            {
                Library.Favorites.Add(alias.Value);
                if (Library.FavoriteAddedAt.TryGetValue(alias.Key, out var time)
                    && time > Library.FavoriteAddedAt.GetValueOrDefault(alias.Value))
                    Library.FavoriteAddedAt[alias.Value] = time;
            }
            Library.FavoriteAddedAt.Remove(alias.Key);
        }
    }

    public void AddToQueue(LibrarySongItem item)
    {
        if (!item.Song.IsAvailable) { ReportProblem("文件不可用，请刷新曲库后重试。"); return; }
        Queue.Add(item.Song); Status = $"已加入待播 · 第 {Queue.Pending.Count} 位 · {item.Title}";
    }
    public void ReportProblem(string message) { Problem = message; Status = "操作未完成"; }

    public void BeginMetadataRead()
    { metadataReads++; Notify(nameof(HasBackgroundActivity)); Notify(nameof(Idle)); NotifyActivity(); }
    public void EndMetadataRead()
    { metadataReads = Math.Max(0, metadataReads - 1); Notify(nameof(HasBackgroundActivity)); Notify(nameof(Idle)); NotifyActivity(); }

    private void RebuildDirectories()
    {
        Directories.Clear();
        foreach (string path in Library.Directories) Directories.Add(new(path));
        NotifyState();
    }

    private void Filter()
    {
        var desired = new List<LibrarySongItem>();
        availableCount = 0;
        foreach (Song song in Library.Songs.OrderBy(s => s.Title, StringComparer.CurrentCultureIgnoreCase))
        {
            if (!songItems.TryGetValue(song.Id, out var item)) songItems[song.Id] = item = new(song);
            bool available = availability.GetValueOrDefault(song.Id);
            item.Update(song, Library.Favorites.Contains(song.Id), available);
            if (available) availableCount++;
            if (!HideUnavailable || available) desired.Add(item);
        }
        SyncRows(Results, desired);
        UpdateFavorites();
        NotifyState();
    }

    private void UpdateFavorites()
    {
        var desired = Results.Where(item => item.IsFavorite)
            .OrderByDescending(item => Library.FavoriteAddedAt.GetValueOrDefault(item.Song.Id))
            .ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.Song.Id, StringComparer.Ordinal).ToList();
        SyncRows(FavoriteResults, desired);
        Notify(nameof(HasNoFavorites)); Notify(nameof(FavoriteEmptyMessage)); Notify(nameof(FavoriteEmptyHint));
    }

    private static void SyncRows(ObservableCollection<LibrarySongItem> rows, IReadOnlyList<LibrarySongItem> desired)
    {
        // 两个页面共享歌曲行资料，刷新或收藏操作不清空整张列表。
        for (int i = 0; i < desired.Count; i++)
        {
            if (i < rows.Count && ReferenceEquals(rows[i], desired[i])) continue;
            int existing = rows.IndexOf(desired[i]);
            if (existing >= 0) rows.Move(existing, i); else rows.Insert(i, desired[i]);
        }
        while (rows.Count > desired.Count) rows.RemoveAt(rows.Count - 1);
    }

    private static Dictionary<string, bool> GetAvailability(IEnumerable<Song> songs) =>
        songs.ToDictionary(song => song.Id, song => song.IsAvailable);
    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static bool IsWithin(string path, string root) => string.Equals(Normalize(path), Normalize(root), StringComparison.OrdinalIgnoreCase)
        || Normalize(path).StartsWith(Path.EndsInDirectorySeparator(Normalize(root)) ? Normalize(root)
            : Normalize(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private void NotifyActivity()
    { Notify(nameof(ActivityLabel)); Notify(nameof(ActivityTooltip)); Notify(nameof(ActivityGlyph)); }
    private void NotifyState()
    {
        foreach (string property in new[] { nameof(CanManage), nameof(ShowHomeHint), nameof(HasNoDirectories),
            nameof(HasNoResults), nameof(HasNoFavorites), nameof(FavoriteEmptyMessage), nameof(FavoriteEmptyHint),
            nameof(DirectorySummary), nameof(LibrarySummary), nameof(EmptyMessage), nameof(EmptyHint) }) Notify(property);
    }
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    public void Dispose() { disposed = true; scanCancellation?.Cancel(); }
}
