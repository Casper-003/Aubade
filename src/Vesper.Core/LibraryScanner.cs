using System.Diagnostics;
using System.Security.Cryptography;

namespace Vesper.Core;

public static class LibraryScanner
{
    public static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".m4v", ".mkv", ".avi", ".wmv", ".mov", ".mp3", ".m4a", ".wav", ".flac" };
    private sealed record MediaFile(string Path, long Length, long Modified);

    public static async Task<ScanResult> ScanAsync(IEnumerable<string> directories, IEnumerable<Song> previous,
        CancellationToken cancellationToken = default, IReadOnlyDictionary<string, FileScanCache>? cache = null,
        IProgress<ScanProgress>? progress = null)
    {
        var timer = Stopwatch.StartNew();
        var lastReport = TimeSpan.MinValue;
        void Report(string phase, int completed, int total, bool force = false)
        {
            if (!force && lastReport != TimeSpan.MinValue && timer.Elapsed - lastReport < TimeSpan.FromMilliseconds(150)) return;
            lastReport = timer.Elapsed;
            progress?.Report(new(phase, completed, total));
        }
        Song[] oldSongs = previous.ToArray();
        var oldById = oldSongs.GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First());
        var oldByPath = new Dictionary<string, Song>(StringComparer.OrdinalIgnoreCase);
        foreach (Song song in oldSongs)
            foreach (string path in song.Sources) oldByPath.TryAdd(path, song);
        foreach (Song song in oldSongs)
            foreach (string path in song.KnownSources ?? []) oldByPath.TryAdd(path, song);
        var stamps = cache is null ? new Dictionary<string, FileScanCache>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, FileScanCache>(cache, StringComparer.OrdinalIgnoreCase);
        var files = new List<MediaFile>();
        var warnings = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Report("正在检查目录", 0, 0, true);
        foreach (string directory in directories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(directory)) { warnings.Add($"目录不可用：{directory}"); continue; }
            var pending = new Stack<string>();
            pending.Push(directory);
            while (pending.TryPop(out string? current))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    foreach (string child in Directory.EnumerateDirectories(current))
                        if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) pending.Push(child);
                    foreach (string path in Directory.EnumerateFiles(current))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!Extensions.Contains(Path.GetExtension(path)) || !seen.Add(path)) continue;
                        try
                        {
                            var info = new FileInfo(path);
                            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                            files.Add(new(path, info.Length, info.LastWriteTimeUtc.Ticks));
                            Report("正在检查目录", files.Count, 0);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        { warnings.Add($"无法读取：{path}（{ex.Message}）"); }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { warnings.Add($"无法扫描：{current}（{ex.Message}）"); }
            }
        }

        var found = new Dictionary<string, Song>();
        var aliases = new Dictionary<string, string>();
        var legacyUnavailableTitles = oldSongs.Where(song => song.Sources.Count == 0 && (song.KnownSources?.Count ?? 0) == 0
            && song.Id.Length == 64 && song.Id.All(Uri.IsHexDigit)).Select(song => song.Title)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        int hashedFiles = 0, processed = 0;
        long hashedBytes = 0;
        bool Unchanged(MediaFile file, out FileScanCache? stamp)
        {
            if (stamps.TryGetValue(file.Path, out var saved) && saved.Length == file.Length && saved.ModifiedUtcTicks == file.Modified)
            { stamp = saved; return true; }
            stamp = null; return false;
        }
        string? ExistingId(MediaFile file, string? hash)
        {
            if (Unchanged(file, out var saved)) return saved!.SongId;
            if (stamps.TryGetValue(file.Path, out var changed) && oldById.TryGetValue(changed.SongId, out var tracked))
            {
                if (hash is not null && hash == changed.ContentHash) return tracked.Id;
                if (tracked.Sources.Count <= 1 && (hash is null || changed.ContentHash is null)) return tracked.Id;
            }
            if (hash is not null && oldById.ContainsKey(hash)) return hash; // 兼容旧版 SHA256 歌曲 ID。
            if (!stamps.ContainsKey(file.Path) && oldByPath.TryGetValue(file.Path, out var old))
            {
                bool legacyHash = old.Id.Length == 64 && old.Id.All(Uri.IsHexDigit);
                if (hash is null || !legacyHash || string.Equals(hash, old.Id, StringComparison.OrdinalIgnoreCase)) return old.Id;
            }
            return null;
        }
        void Register(List<MediaFile> group, string? hash)
        {
            string[] existingIds = group.Select(file => ExistingId(file, hash)).OfType<string>().Distinct().ToArray();
            string id = existingIds.FirstOrDefault(candidate => oldById.ContainsKey(candidate))
                ?? existingIds.FirstOrDefault() ?? Guid.NewGuid().ToString("N");
            // 两个不同内容组不能因旧路径记录而共用同一 ID。
            if (found.ContainsKey(id)) id = Guid.NewGuid().ToString("N");
            string title = oldById.TryGetValue(id, out var old) ? old.Title : Path.GetFileNameWithoutExtension(group[0].Path);
            var knownSources = group.Select(file => file.Path).Concat(existingIds.Where(oldById.ContainsKey)
                .SelectMany(previousId => oldById[previousId].Sources.Concat(oldById[previousId].KnownSources ?? [])))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            found[id] = new(id, title, group.Select(file => file.Path).ToList()) { KnownSources = knownSources };
            foreach (string previousId in existingIds)
                if (previousId != id && !found.ContainsKey(previousId)) aliases[previousId] = id;
            foreach (MediaFile file in group)
            {
                string? knownHash = hash;
                if (knownHash is null && Unchanged(file, out var saved)) knownHash = saved!.ContentHash;
                stamps[file.Path] = new(file.Length, file.Modified, id, knownHash);
            }
        }

        async Task<string?> ReadHashAsync(MediaFile file, string phase)
        {
            try
            {
                if (Unchanged(file, out var saved) && saved!.ContentHash is { Length: > 0 } known) return known;
                Report(phase, processed, files.Count);
                await using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                string hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
                hashedFiles++; hashedBytes += stream.Length;
                var after = new FileInfo(file.Path);
                if (after.Length != file.Length || after.LastWriteTimeUtc.Ticks != file.Modified)
                { warnings.Add($"文件在扫描中发生变化，请刷新后重试：{file.Path}"); return null; }
                return hash;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { warnings.Add($"无法读取：{file.Path}（{ex.Message}）"); return null; }
        }

        foreach (var sizeGroup in files.GroupBy(file => file.Length))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidates = sizeGroup.ToList();
            if (candidates.Count == 1)
            {
                string? hash = null;
                MediaFile file = candidates[0];
                // 旧版失效记录没有保留来源路径；同名文件恢复时核对一次，以找回收藏关联。
                if (ExistingId(file, null) is null && legacyUnavailableTitles.Contains(Path.GetFileNameWithoutExtension(file.Path)))
                {
                    hash = await ReadHashAsync(file, "正在恢复历史歌曲关联");
                    if (hash is null) { processed++; continue; }
                }
                Register(candidates, hash); // 长度不同必然不是完全相同的文件，通常无需读取视频内容。
                processed++;
                Report("正在收录歌曲", processed, files.Count);
                continue;
            }
            var byHash = new Dictionary<string, List<MediaFile>>();
            foreach (MediaFile file in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? hash = await ReadHashAsync(file, "正在核对可能重复的文件");
                if (hash is not null)
                {
                    if (!byHash.TryGetValue(hash, out var matching)) byHash[hash] = matching = [];
                    matching.Add(file);
                }
                processed++;
                Report("正在核对歌曲", processed, files.Count);
            }
            foreach (var pair in byHash) Register(pair.Value, pair.Key);
        }
        foreach (Song old in oldSongs)
            if (!found.ContainsKey(old.Id) && !aliases.ContainsKey(old.Id)) found[old.Id] = old with { Sources = [] };
        Report("扫描完成", files.Count, files.Count, true);
        return new(found.Values.OrderBy(s => s.Title, StringComparer.CurrentCultureIgnoreCase).ToList(), warnings,
            stamps, aliases, new(files.Count, hashedFiles, hashedBytes, timer.Elapsed));
    }
}
