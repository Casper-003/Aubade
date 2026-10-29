using System.Text.Json;
using Vesper.App;
using Vesper.Core;

internal static class FavoriteChecks
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        string folder = Path.Combine(root, "favorite-media");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "A.mp4"), "a");
        await File.WriteAllTextAsync(Path.Combine(folder, "B.mp4"), "bb");
        await File.WriteAllTextAsync(Path.Combine(folder, "C.mp4"), "ccc");
        string path = Path.Combine(root, "favorite-state.json");
        using var model = new MainViewModel(path, timeProvider: new FixedClock());
        await model.InitializeAsync();
        check(model.HasNoFavorites && model.FavoriteEmptyHint.Contains("心形"),
            "收藏为空时明确说明从曲库点击心形收藏");
        await model.AddDirectoryAsync(folder);
        LibrarySongItem a = model.Results.Single(item => item.Title == "A");
        LibrarySongItem b = model.Results.Single(item => item.Title == "B");
        LibrarySongItem c = model.Results.Single(item => item.Title == "C");
        await model.ToggleFavoriteAsync(b);
        await model.ToggleFavoriteAsync(a);
        await model.ToggleFavoriteAsync(c);
        check(ReferenceEquals(model.LatestFavorite, c), "收藏页页头使用最后收藏的歌曲资料与封面实例");
        check(model.FavoriteResults.Select(item => item.Title).SequenceEqual(["C", "A", "B"])
            && model.Results.Select(item => item.Title).SequenceEqual(["A", "B", "C"]),
            "同一时钟刻度内收藏仍按操作时间倒序，曲库原排序不变");
        a.Artist = "测试艺人"; a.Resolution = "1080p";
        check(ReferenceEquals(model.FavoriteResults[1], a) && model.FavoriteResults[1].Artist == "测试艺人"
            && model.FavoriteResults[1].Resolution == "1080p",
            "收藏与曲库共享行实例，异步读取的画质和艺人资料同步显示");
        model.AddToQueue(model.FavoriteResults[0]);
        check(model.Queue.Pending.Single().SongId == c.Song.Id,
            "收藏页歌曲可直接加入现有待播队列");
        DateTimeOffset oldB = model.Library.FavoriteAddedAt[b.Song.Id];
        await model.ToggleFavoriteAsync(b);
        check(!b.IsFavorite && model.FavoriteResults.Count == 2 && !model.Library.FavoriteAddedAt.ContainsKey(b.Song.Id),
            "取消收藏立即移出收藏页，并清理该曲收藏时间");
        await model.ToggleFavoriteAsync(b);
        string[] expected = [b.Song.Id, c.Song.Id, a.Song.Id];
        check(model.FavoriteResults.Select(item => item.Song.Id).SequenceEqual(expected)
            && model.Library.FavoriteAddedAt[b.Song.Id] > oldB,
            "重新收藏回到首位，不沿用之前的收藏时间");
        using var reopened = new MainViewModel(path);
        await reopened.InitializeAsync();
        check(reopened.FavoriteResults.Select(item => item.Song.Id).SequenceEqual(expected),
            "重启及重新扫描后保留收藏时间与倒序排列");
        var savedTimes = new Dictionary<string, DateTimeOffset>(model.Library.FavoriteAddedAt);
        await model.RemoveDirectoryAsync(folder);
        check(model.HasNoFavorites && model.Library.Favorites.Count == 3
            && model.FavoriteEmptyMessage == "收藏的歌曲暂不可用",
            "移除来源后保留收藏，全部隐藏时说明歌曲暂不可用");
        model.HideUnavailable = false;
        check(model.FavoriteResults.Select(item => item.Song.Id).SequenceEqual(expected)
            && model.FavoriteResults.All(item => item.IsUnavailable && item.RowOpacity == 0.5),
            "关闭隐藏不可用设置时，收藏按原顺序灰显");
        model.HideUnavailable = true;
        await model.AddDirectoryAsync(folder);
        check(model.FavoriteResults.Select(item => item.Song.Id).SequenceEqual(expected)
            && model.FavoriteResults.All(item => item.IsAvailable)
            && savedTimes.All(pair => model.Library.FavoriteAddedAt[pair.Key] == pair.Value),
            "恢复来源后原收藏重新显示，歌曲身份、时间与排序保持不变");

        // 旧版记录没有收藏时间，仍能加载；未记录时间的旧项采用稳定歌名顺序。
        string legacyPath = Path.Combine(root, "favorite-legacy.json");
        await File.WriteAllTextAsync(legacyPath, JsonSerializer.Serialize(new
        {
            Directories = new[] { folder }, Songs = model.Library.Songs,
            Favorites = new[] { c.Song.Id, a.Song.Id }
        }));
        using var legacy = new MainViewModel(legacyPath);
        await legacy.InitializeAsync();
        await legacy.ToggleFavoriteAsync(legacy.Results.Single(item => item.Song.Id == b.Song.Id));
        check(!legacy.ReadOnly && legacy.FavoriteResults.Select(item => item.Title).SequenceEqual(["B", "A", "C"]),
            "兼容无时间的旧收藏，新收藏排在旧项之前且不伪造旧时间");

        Task[] toggles = Enumerable.Range(0, 9).Select(_ => model.ToggleFavoriteAsync(a)).ToArray();
        await Task.WhenAll(toggles);
        LibraryState persisted = await model.Store.LoadAsync();
        check(!a.IsFavorite && !model.FavoriteResults.Contains(a) && !persisted.Favorites.Contains(a.Song.Id)
            && !persisted.FavoriteAddedAt.ContainsKey(a.Song.Id),
            "连续快速点击收藏后，列表、心形和最终保存状态一致");

        string temporary = path + ".tmp";
        Directory.CreateDirectory(temporary);
        await model.ToggleFavoriteAsync(a);
        check(model.HasProblem && !a.IsFavorite && !model.FavoriteResults.Contains(a)
            && !model.Library.Favorites.Contains(a.Song.Id),
            "收藏写入失败时恢复原状态，不留下未保存的收藏行");
        DateTimeOffset oldTime = model.Library.FavoriteAddedAt[c.Song.Id];
        await model.ToggleFavoriteAsync(c);
        check(c.IsFavorite && model.FavoriteResults.Contains(c) && model.Library.FavoriteAddedAt[c.Song.Id] == oldTime,
            "取消收藏写入失败时恢复原收藏时间和排列位置");
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }
}
