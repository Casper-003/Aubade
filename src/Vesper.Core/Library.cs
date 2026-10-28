using System.Security.Cryptography;
using System.Text.Json;

namespace Vesper.Core;

public sealed record Song(string Id, string Title, List<string> Sources)
{
    public string Artist => "未知艺人";
    public string? AvailablePath => Sources.FirstOrDefault(File.Exists);
    public bool IsAvailable => AvailablePath is not null;
}

public sealed class LibraryState
{
    public List<string> Directories { get; set; } = [];
    public List<Song> Songs { get; set; } = [];
    public HashSet<string> Favorites { get; set; } = [];
}

public sealed class LibraryStore(string path)
{
    private readonly SemaphoreSlim saveLock = new(1, 1);
    public async Task<LibraryState> LoadAsync()
    {
        if (!File.Exists(path)) return new();
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<LibraryState>(stream)
            ?? throw new InvalidDataException("曲库记录为空，请先备份并检查本机曲库文件。");
    }

    public async Task SaveAsync(LibraryState state)
    {
        string snapshot = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
        await saveLock.WaitAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            string temporary = path + ".tmp";
            await File.WriteAllTextAsync(temporary, snapshot);
            File.Move(temporary, path, overwrite: true);
        }
        finally { saveLock.Release(); }
    }
}

public sealed record ScanResult(List<Song> Songs, List<string> Warnings);

public static class LibraryScanner
{
    // 候选媒体文件；能否解码以 Windows 播放器的实际结果为准。
    public static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".m4v", ".mkv", ".avi", ".wmv", ".mov", ".mp3", ".m4a", ".wav", ".flac" };

    public static async Task<ScanResult> ScanAsync(IEnumerable<string> directories,
        IEnumerable<Song> previous, CancellationToken cancellationToken = default)
    {
        var found = new Dictionary<string, Song>();
        var warnings = new List<string>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                    // 不跟随目录链接，避免循环扫描及越出用户所选目录。
                    foreach (string child in Directory.GetDirectories(current))
                        if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) pending.Push(child);
                    foreach (string file in Directory.GetFiles(current))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!Extensions.Contains(Path.GetExtension(file)) || !seenPaths.Add(file)) continue;
                        try
                        {
                            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read,
                                FileShare.ReadWrite | FileShare.Delete, 131072,
                                FileOptions.Asynchronous | FileOptions.SequentialScan);
                            string id = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
                            if (found.TryGetValue(id, out Song? song)) song.Sources.Add(file);
                            else found[id] = new(id, Path.GetFileNameWithoutExtension(file), [file]);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        { warnings.Add($"无法读取：{file}（{ex.Message}）"); }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { warnings.Add($"无法扫描：{current}（{ex.Message}）"); }
            }
        }
        // 保留失效歌曲及其 ID，收藏关系不会因目录暂时不可用而丢失。
        foreach (Song old in previous)
            if (!found.ContainsKey(old.Id)) found[old.Id] = old with { Sources = [] };
        return new(found.Values.OrderBy(s => s.Title, StringComparer.CurrentCultureIgnoreCase).ToList(), warnings);
    }
}
