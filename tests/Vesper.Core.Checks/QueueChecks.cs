using Vesper.Core;

internal static class QueueChecks
{
    public static void Run(string availablePath, Action<bool, string> check)
    {
        var queue = new PlaybackQueue();
        var playing = queue.Add(new("current", "正在演唱", [availablePath]));
        var first = queue.Add(new("first", "第一首", [availablePath]));
        var second = queue.Add(new("second", "第二首", [availablePath]));
        var repeat = queue.Add(new("first", "第一首", [availablePath]));
        var changes = new List<string?>();
        queue.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
        queue.StartNext([new("current", "正在演唱", [availablePath])], out _);
        queue.MarkPlaying();
        check(changes.Contains(nameof(PlaybackQueue.Current)) && changes.Contains(nameof(PlaybackQueue.State)),
            "当前歌曲和演唱状态变化可通知队列面板更新");

        check(queue.InsertNext(repeat.Id) && queue.Pending.Select(entry => entry.Id).SequenceEqual([repeat.Id, first.Id, second.Id])
            && queue.Current?.Id == playing.Id && queue.State == PlaybackState.Playing,
            "插播按条目身份移动到待播首位，保留其他顺序与当前演唱");
        check(!queue.InsertNext(repeat.Id) && !queue.MovePending(playing.Id, 0)
            && !queue.MovePending(second.Id, -1) && !queue.MovePending(second.Id, queue.Pending.Count),
            "首位重复插播、当前歌曲及无效拖动目标不会修改待播队列");

        check(queue.MovePending(repeat.Id, 2) && queue.Pending.Select(entry => entry.Id).SequenceEqual([first.Id, second.Id, repeat.Id]),
            "拖动排序保留重复点歌的独立条目，按目标位置调整顺序");
        var last = queue.Add(new("last", "新加入", [availablePath]));
        check(queue.Pending[^1].Id == last.Id && queue.Pending[0].Id == first.Id,
            "手动排序后新点歌曲仍追加到队尾");

        QueueRemoval? removal = queue.RemovePending(repeat.Id);
        check(removal is not null && removal.Entry.Id == repeat.Id && removal.OriginalIndex == 2
            && queue.Pending.Any(entry => entry.Id == first.Id) && queue.Current?.Id == playing.Id,
            "移除只作用于选中的待播条目，保留同曲其他条目与正在演唱歌曲");
        check(removal is not null && queue.RestorePending(removal) && queue.Pending[2].Id == repeat.Id
            && !queue.RestorePending(removal),
            "撤销恢复原条目与原位置，同一条移除记录不会重复恢复");
        check(queue.RemovePending(playing.Id) is null && queue.RemovePending(Guid.NewGuid()) is null,
            "待播移除不会删除当前歌曲或过期条目");

        queue.MarkPaused();
        check(queue.MovePending(last.Id, 0) && queue.State == PlaybackState.Paused && queue.Current?.Id == playing.Id,
            "暂停期间整理待播顺序不会恢复播放或切换当前歌曲");
    }

    public static async Task RunPersistenceAsync(string root, Action<bool, string> check)
    {
        string directory = Path.Combine(root, "queue-media"); Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "排播甲.mp4"), "queue-a");
        await File.WriteAllTextAsync(Path.Combine(directory, "排播乙.mp4"), "queue-b-different");
        string path = Path.Combine(root, "queue-state", "library.json");
        using var model = new Vesper.App.MainViewModel(path);
        await model.InitializeAsync(); await model.AddDirectoryAsync(directory);
        var a = model.Results.Single(row => row.Title == "排播甲");
        var b = model.Results.Single(row => row.Title == "排播乙");
        model.AddToQueue(a); model.AddToQueue(b); model.AddToQueue(a);
        model.Queue.MovePending(model.Queue.Pending[^1].Id, 0);
        Guid[] order = model.Queue.Pending.Select(entry => entry.Id).ToArray();
        check(await model.FlushQueueAsync(), "队列变更可等待写入完成，关闭时保留最新顺序");
        using var reopened = new Vesper.App.MainViewModel(path);
        await reopened.InitializeAsync();
        check(reopened.Queue.Pending.Select(entry => entry.Id).SequenceEqual(order)
            && reopened.Queue.Pending[0].SongId == reopened.Queue.Pending[1].SongId
            && reopened.Queue.State == PlaybackState.Idle && reopened.Queue.Current is null,
            "重启恢复手动顺序及独立的重复条目，保持等待手动开唱");

        model.Queue.StartNext(model.Library.Songs, out _); model.Queue.MarkPaused();
        Guid current = model.Queue.Current!.Id;
        check(await model.FlushQueueAsync(), "未唱完的当前条目与其他待播歌曲一起保存");
        using var interrupted = new Vesper.App.MainViewModel(path);
        await interrupted.InitializeAsync();
        check(interrupted.Queue.Current is null && interrupted.Queue.State == PlaybackState.Idle
            && interrupted.Queue.Pending[0].Id == current && interrupted.Queue.Pending.Select(entry => entry.Id).SequenceEqual(order),
            "关闭时未唱完的当前歌曲重启后回到队首，不保存暂停进度或自动续唱");

        var bad = new LibraryState { PendingQueue = [new(Guid.Empty, "bad", "损坏条目")] };
        string badPath = Path.Combine(root, "bad-queue.json");
        await new LibraryStore(badPath).SaveAsync(bad);
        string original = await File.ReadAllTextAsync(badPath);
        using var invalid = new Vesper.App.MainViewModel(badPath);
        await invalid.InitializeAsync();
        check(invalid.ReadOnly && invalid.HasProblem && await File.ReadAllTextAsync(badPath) == original,
            "损坏队列记录明确报错并保护原文件，避免静默覆盖待播数据");

        string blockedPath = Path.Combine(root, "queue-save-target-is-directory"); Directory.CreateDirectory(blockedPath);
        using var blocked = new Vesper.App.MainViewModel(blockedPath);
        await blocked.InitializeAsync(); blocked.Queue.Add(new("unsaved", "未保存条目", []));
        check(!await blocked.FlushQueueAsync() && blocked.Queue.Pending.Count == 1
            && blocked.Problem.StartsWith("待播队列保存失败", StringComparison.Ordinal),
            "队列保存失败明确报告且保留本次条目，关闭流程可等待用户处理后重试");
    }
}
