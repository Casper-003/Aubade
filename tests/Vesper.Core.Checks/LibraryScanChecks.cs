using System.Diagnostics;
using Vesper.App;
using Vesper.Core;

internal static class LibraryScanChecks
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        string media = Path.Combine(root, "performance");
        Directory.CreateDirectory(media);
        for (int i = 0; i < 1007; i++)
            await File.WriteAllBytesAsync(Path.Combine(media, $"song-{i:D4}.mp4"), new byte[65536 + i]);
        var first = await LibraryScanner.ScanAsync([media], []);
        var second = await LibraryScanner.ScanAsync([media], first.Songs, cache: first.Cache);
        check(first.Songs.Count == 1007 && first.Statistics.HashedBytes == 0 && second.Statistics.HashedBytes == 0,
            "1007 个不同长度文件首次及再次扫描均不读取媒体全文");
        check(first.Songs.Select(song => song.Id).SequenceEqual(second.Songs.Select(song => song.Id)),
            "缓存刷新保持歌曲 ID 与已有记录稳定");
        Console.WriteLine($"BENCH synthetic local files: 1007 files, first={first.Statistics.Elapsed.TotalMilliseconds:F0} ms, cached={second.Statistics.Elapsed.TotalMilliseconds:F0} ms, media bytes read=0.");

        string originalPath = Path.Combine(media, "song-0000.mp4");
        Song original = first.Songs.Single(song => song.Sources.Contains(originalPath));
        string duplicatePath = Path.Combine(media, "duplicate.mp4");
        File.Copy(originalPath, duplicatePath);
        var duplicate = await LibraryScanner.ScanAsync([media], second.Songs, cache: second.Cache);
        check(duplicate.Songs.Count == 1007 && duplicate.Songs.Single(song => song.Id == original.Id).Sources.Count == 2
            && duplicate.Statistics.HashedFiles == 2,
            "新增副本时仅完整核对同长度候选，合并内容并保留原歌曲 ID");
        var cachedDuplicate = await LibraryScanner.ScanAsync([media], duplicate.Songs, cache: duplicate.Cache);
        check(cachedDuplicate.Statistics.HashedFiles == 0, "重复文件未变化时复用哈希，刷新不重复读取原文件");
        byte[] replacement = new byte[65536]; replacement[0] = 1;
        await File.WriteAllBytesAsync(duplicatePath, replacement);
        File.SetLastWriteTimeUtc(duplicatePath, DateTime.UtcNow.AddSeconds(2));
        var changed = await LibraryScanner.ScanAsync([media], cachedDuplicate.Songs, cache: cachedDuplicate.Cache);
        check(changed.Songs.Count(song => song.IsAvailable) == 1008 && changed.Statistics.HashedFiles == 1,
            "同长度但内容变化时重新核对，仅重读变化文件，禁止误合并");
        var cacheStore = new LibraryStore(Path.Combine(root, "cache-state.json"));
        var saved = new LibraryState { Directories = [media], Songs = duplicate.Songs, ScanCache = duplicate.Cache, Favorites = [original.Id] };
        await cacheStore.SaveAsync(saved);
        var loaded = await cacheStore.LoadAsync();
        check(loaded.ScanCache.Count == 1008 && loaded.Favorites.Contains(original.Id), "扫描缓存与收藏随本机曲库持久化");

        string one = Path.Combine(root, "queued-one"), two = Path.Combine(root, "queued-two");
        Directory.CreateDirectory(one); Directory.CreateDirectory(two);
        await File.WriteAllTextAsync(Path.Combine(one, "one.mp4"), "one");
        await File.WriteAllTextAsync(Path.Combine(two, "two.mp4"), "two-two");
        bool hold = false;
        int running = 0, maximumRunning = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<ScanResult> Controlled(string[] dirs, Song[] previous, IReadOnlyDictionary<string, FileScanCache> cache,
            IProgress<ScanProgress> progress, CancellationToken token)
        {
            int active = Interlocked.Increment(ref running);
            maximumRunning = Math.Max(maximumRunning, active);
            try
            {
                if (hold) { started.TrySetResult(); await release.Task; }
                // 故意让过期任务返回，验证目录版本检查仍能拒绝旧结果。
                return await LibraryScanner.ScanAsync(dirs, previous, cache: cache, progress: progress);
            }
            finally { Interlocked.Decrement(ref running); }
        }
        using var model = new MainViewModel(Path.Combine(root, "queued-state.json"), Controlled);
        await model.InitializeAsync();
        hold = true;
        Task refresh = model.RefreshAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        check(model.Busy && model.CanManage, "后台扫描期间添加、管理与刷新操作保持可用");
        Task addOne = model.AddDirectoryAsync(one);
        Task addTwo = model.AddDirectoryAsync(two);
        Task removeOne = model.RemoveDirectoryAsync(one);
        check(model.Directories.Count == 1 && model.Directories[0].Path == two,
            "扫描未完成时仍可连续添加多个目录并立即移除其中一个");
        hold = false; release.TrySetResult();
        await Task.WhenAll(refresh, addOne, addTwo, removeOne).WaitAsync(TimeSpan.FromSeconds(10));
        check(maximumRunning == 1 && !model.Busy && model.Results.Count == 1 && model.Results[0].Title == "two",
            "扫描串行合并调度，拒绝过期结果，不把已移除的目录重新加回");
        var row = model.Results.Single();
        await model.ToggleFavoriteAsync(row);
        await model.RefreshAsync();
        check(ReferenceEquals(row, model.Results.Single()) && row.IsFavorite,
            "刷新保留原歌曲行实例与扫描期间的收藏，不重建整张列表");

        string legacyPath = Path.Combine(root, "legacy.mp4");
        await File.WriteAllTextAsync(legacyPath, "legacy-content");
        string legacyId = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(legacyPath)));
        Song legacy = new(legacyId, "保留的歌曲名称", [legacyPath]);
        string legacyFolder = Path.Combine(root, "legacy-folder"); Directory.CreateDirectory(legacyFolder);
        string moved = Path.Combine(legacyFolder, "legacy.mp4"); File.Move(legacyPath, moved);
        legacy = legacy with { Sources = [moved] };
        var migrated = await LibraryScanner.ScanAsync([legacyFolder], [legacy]);
        check(migrated.Songs.Single().Id == legacyId && migrated.Songs.Single().Title == legacy.Title
            && migrated.Statistics.HashedFiles == 0, "旧版 SHA256 ID 与用户歌曲名称兼容迁移，不重新通读媒体");
        File.SetLastWriteTimeUtc(moved, DateTime.UtcNow.AddSeconds(5));
        var touched = await LibraryScanner.ScanAsync([legacyFolder], migrated.Songs, cache: migrated.Cache);
        check(touched.Songs.Single().Id == legacyId, "单一来源文件时间戳变化后仍保留原歌曲关联");
        var unavailableLegacy = legacy with { Title = "legacy", Sources = [] };
        var restoredLegacy = await LibraryScanner.ScanAsync([legacyFolder], [unavailableLegacy]);
        check(restoredLegacy.Songs.Single().Id == legacyId && restoredLegacy.Songs.Single().IsAvailable,
            "旧版已失效且无路径的同名歌曲恢复时核对内容，找回原歌曲关联");

        string separateOne = Path.Combine(root, "separate-one"), separateTwo = Path.Combine(root, "separate-two");
        Directory.CreateDirectory(separateOne); Directory.CreateDirectory(separateTwo);
        await File.WriteAllTextAsync(Path.Combine(separateOne, "same.mp4"), "identical");
        await File.WriteAllTextAsync(Path.Combine(separateTwo, "same.mp4"), "identical");
        var catalogOne = await LibraryScanner.ScanAsync([separateOne], []);
        var catalogTwo = await LibraryScanner.ScanAsync([separateTwo], []);
        var mergedCache = new Dictionary<string, FileScanCache>(catalogOne.Cache, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in catalogTwo.Cache) mergedCache[pair.Key] = pair.Value;
        string mergeStorePath = Path.Combine(root, "merge-state.json");
        await new LibraryStore(mergeStorePath).SaveAsync(new LibraryState
        {
            Directories = [separateOne, separateTwo], Songs = [.. catalogOne.Songs, .. catalogTwo.Songs],
            Favorites = [catalogOne.Songs[0].Id, catalogTwo.Songs[0].Id], ScanCache = mergedCache,
            Details = new()
            {
                [catalogOne.Songs[0].Id] = new(Corrections: new("较早修正", "艺人", "专辑", DateTimeOffset.UnixEpoch)),
                [catalogTwo.Songs[0].Id] = new(Online: new(Year: "2020"),
                    Corrections: new("最新修正", "修正艺人", "修正专辑", DateTimeOffset.UnixEpoch.AddDays(1)))
            },
            FavoriteAddedAt = new()
            {
                [catalogOne.Songs[0].Id] = DateTimeOffset.UnixEpoch,
                [catalogTwo.Songs[0].Id] = DateTimeOffset.UnixEpoch.AddDays(1)
            }
        });
        using var mergedModel = new MainViewModel(mergeStorePath);
        QueueEntry pending = mergedModel.Queue.Add(catalogTwo.Songs[0]);
        await mergedModel.InitializeAsync();
        check(mergedModel.Results.Count == 1 && mergedModel.Results[0].IsFavorite
            && mergedModel.Queue.Pending[0].SongId == mergedModel.Results[0].Song.Id
            && mergedModel.Queue.Pending[0].Id == pending.Id,
            "独立目录中的相同歌曲合并后，收藏与已有待播条目关联同步迁移");
        check(mergedModel.FavoriteResults.Count == 1 && mergedModel.Library.FavoriteAddedAt.Count == 1
            && mergedModel.Library.FavoriteAddedAt[mergedModel.FavoriteResults[0].Song.Id] == DateTimeOffset.UnixEpoch.AddDays(1),
            "重复歌曲合并时保留较新的收藏时间，收藏页仅显示一项");
        check(mergedModel.Results.Single().Title == "最新修正" && mergedModel.Results.Single().Year == "2020"
            && ReferenceEquals(mergedModel.FindSong(catalogTwo.Songs[0].Id), mergedModel.Results.Single()),
            "重复歌曲合并保留最近的资料修正和联网资料，旧详情入口解析到合并后的歌曲");
    }
}
