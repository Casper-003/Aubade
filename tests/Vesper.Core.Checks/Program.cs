using Vesper.Core;

string root = Path.Combine(Path.GetTempPath(), "VesperChecks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int count = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + description);
    Console.WriteLine("PASS: " + description); count++;
}

try
{
    string first = Path.Combine(root, "disc-one");
    string second = Path.Combine(root, "disc-two");
    Directory.CreateDirectory(first); Directory.CreateDirectory(second);
    string a = Path.Combine(first, "测试歌曲.mp4");
    string b = Path.Combine(second, "同内容副本.mp4");
    await File.WriteAllTextAsync(a, "same media bytes");
    File.Copy(a, b);
    string other = Path.Combine(first, "下一首.mp4");
    await File.WriteAllTextAsync(other, "different media bytes");
    await File.WriteAllTextAsync(Path.Combine(first, "说明.txt"), "ignore");
    ScanResult scan = await LibraryScanner.ScanAsync([first, second, first], []);
    Check(scan.Songs.Count == 2 && scan.Songs.Single(s => s.Title == "测试歌曲").Sources.Count == 2,
        "相同内容跨目录合并，重复目录不重复收录，跳过非媒体文件");
    Song song = scan.Songs.Single(s => s.Title == "测试歌曲");
    Song next = scan.Songs.Single(s => s.Title == "下一首");
    File.Delete(a);
    Check(song.IsAvailable && song.AvailablePath == b, "一个来源丢失后使用另一有效来源");
    var state = new LibraryState { Directories = [first, second], Songs = scan.Songs, Favorites = [song.Id] };
    var store = new LibraryStore(Path.Combine(root, "data", "library.json"));
    await Task.WhenAll(store.SaveAsync(state), store.SaveAsync(state));
    LibraryState restored = await store.LoadAsync();
    Check(restored.Favorites.Contains(song.Id) && restored.Songs.Count == 2,
        "曲库与收藏持久化，并发保存不会破坏记录");
    ScanResult removedDirectory = await LibraryScanner.ScanAsync([first], scan.Songs);
    Check(!removedDirectory.Songs.Single(s => s.Id == song.Id).IsAvailable && File.Exists(b),
        "移除最后有效目录后保留不可用记录，不删除原文件");
    ScanResult recovered = await LibraryScanner.ScanAsync([first, second], removedDirectory.Songs);
    Check(recovered.Songs.Single(s => s.Id == song.Id).IsAvailable, "重新添加目录恢复原歌曲 ID");
    var queue = new PlaybackQueue();
    QueueEntry original = queue.Add(song); queue.Add(next); queue.Add(song);
    Check(queue.Pending.Count == 3 && queue.Pending[0].Id != queue.Pending[2].Id,
        "同曲重复点歌具有独立队列条目");
    Check(queue.StartNext(recovered.Songs, out _)?.Id == song.Id && queue.Pending.Count == 2,
        "开始演唱只取出队首，当前歌曲与待播分开");
    queue.MarkPlaying(); queue.MarkPaused();
    Check(queue.State == PlaybackState.Paused && queue.Current?.Id == original.Id,
        "暂停保持当前歌曲与待播顺序");
    queue.ExitSession();
    Check(queue.Current is null && queue.Pending[0].Id == original.Id && queue.Pending.Count == 3,
        "退出本场将当前歌曲放回队首，保留原顺序");
    File.Delete(b);
    Check(queue.StartNext(recovered.Songs, out int skipped)?.Id == next.Id && skipped == 1,
        "轮到已失效歌曲时跳过，播放下一有效歌曲");
    Check(queue.StartNext(recovered.Songs, out skipped) is null && skipped == 1
        && queue.State == PlaybackState.Idle && queue.Pending.Count == 0,
        "全部剩余歌曲失效时停止推进，进入等待状态");
    queue.Add(next);
    Check(queue.State == PlaybackState.Idle && queue.Current is null,
        "队列从空变为非空不会自动开唱");
    await File.WriteAllTextAsync(Path.Combine(root, "data", "library.json"), "{ broken json");
    bool rejected = false;
    try { await store.LoadAsync(); } catch (System.Text.Json.JsonException) { rejected = true; }
    Check(rejected, "损坏记录明确报错，不静默覆盖为空曲库");
    Console.WriteLine($"{count} checks passed.");
}
finally
{
    // 仅清理本测试通过 Guid 创建的临时目录。
    if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
        && Path.GetFileName(root).StartsWith("VesperChecks-", StringComparison.Ordinal))
        Directory.Delete(root, recursive: true);
}
