using System.Collections.Concurrent;
using System.Text.Json;

namespace Vesper.Core;

public sealed partial class AdditionalSongSources(HttpClient http, MusicBrainzClient musicBrainz, LrcLibClient lrcLib)
{
    private sealed record Entry(SongMatch Match, long Id, double Duration);
    private readonly ConcurrentDictionary<string, Task<List<Entry>>> searches = new();
    private async Task<JsonDocument> GetAsync(string url, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (request.RequestUri!.Host == "music.163.com") request.Headers.Referrer = new("https://music.163.com/");
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 Vesper/0.1");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var response = await http.SendAsync(request, timeout.Token);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
    }
    private async Task<List<Entry>> SearchNetease(string title, CancellationToken token)
    {
        using var json = await GetAsync("https://music.163.com/api/cloudsearch/pc?s=" + Uri.EscapeDataString(title) + "&type=1&limit=20", token);
        if (!json.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
            throw new HttpRequestException("备用歌曲源暂不可用。");
        var entries = new List<Entry>();
        if (!result.TryGetProperty("songs", out var songs)) return entries;
        foreach (var song in songs.EnumerateArray())
        {
            long id = song.GetProperty("id").GetInt64();
            string name = MusicBrainzClient.Text(song, "name");
            if (!SongNameMatching.Same(name, title)) continue;
            string artist = string.Join(" / ", song.GetProperty("ar").EnumerateArray().Select(a => MusicBrainzClient.Text(a, "name")));
            var album = song.GetProperty("al");
            string cover = MusicBrainzClient.Text(album, "picUrl").Replace("http://", "https://");
            long published = song.TryGetProperty("publishTime", out var publishedAt) && publishedAt.TryGetInt64(out long date) ? date : 0;
            string year = published > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(published).Year.ToString() : "";
            entries.Add(new(new("netease:" + id, album.GetProperty("id").ToString(), name, artist,
                MusicBrainzClient.Text(album, "name"), year, "", CoverUrl: cover), id, song.GetProperty("dt").GetDouble() / 1000));
        }
        return entries;
    }
    private async Task<List<Entry>> Entries(string title, CancellationToken token)
    {
        var task = searches.GetOrAdd(title, _ => SearchNetease(title, token));
        try { return await task; }
        catch { searches.TryRemove(title, out _); throw; }
    }
    private static async Task<IReadOnlyList<T>> Combine<T>(params Func<Task<IReadOnlyList<T>>>[] sources)
    {
        var outcomes = await Task.WhenAll(sources.Select(async source =>
        {
            try { return (Rows: await source(), Error: (Exception?)null); }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidDataException or OperationCanceledException
                or InvalidOperationException or KeyNotFoundException or FormatException)
            { return (Rows: (IReadOnlyList<T>)Array.Empty<T>(), Error: ex); }
        }));
        var rows = outcomes.SelectMany(result => result.Rows).ToArray();
        if (rows.Length == 0 && outcomes.Any(result => result.Error is not null)) throw new HttpRequestException("检索来源暂不可用。");
        return rows;
    }
    public Task<IReadOnlyList<SongMatch>> SearchMetadata(string title, CancellationToken token) => Combine<SongMatch>(
        () => musicBrainz.SearchAsync(title, "", token), async () => (await Entries(title, token)).Select(entry => entry.Match).ToList(),
        () => SearchDeezer(title, token));
    public Task<SongMetadata> GetMetadata(SongMatch match, CancellationToken token) => match.RecordingId.StartsWith("deezer:")
        ? Task.FromResult(new SongMetadata(match.Title, match.Artist, match.Album,
            SourceUrl: "https://www.deezer.com/track/" + match.RecordingId[7..], CoverUrl: match.CoverUrl))
        : match.RecordingId.StartsWith("netease:")
        ? Task.FromResult(new SongMetadata(match.Title, match.Artist, match.Album, Year: match.Year,
            SourceUrl: "https://music.163.com/song?id=" + match.RecordingId[8..], CoverUrl: match.CoverUrl))
        : musicBrainz.GetMetadataAsync(match, token);
    public Task<IReadOnlyList<LyricsCandidate>> SearchLyricsWithArtist(string title, string artist, CancellationToken token) => SearchLyrics(title, token, artist);
    public Task<IReadOnlyList<LyricsCandidate>> SearchLyrics(string title, CancellationToken token, string artistHint = "") => Combine<LyricsCandidate>(
        () => lrcLib.SearchAsync(title, token), async () =>
        {
            var entries = await Entries(title, token);
            var lyrics = new List<LyricsCandidate>();
            foreach (var entry in entries.Where(entry => artistHint.Length == 0 || SongNameMatching.SameArtist(entry.Match.Artist, artistHint)).Take(3))
            {
                using var json = await GetAsync($"https://music.163.com/api/song/lyric?id={entry.Id}&lv=-1&kv=-1&tv=-1", token);
                if (!json.RootElement.TryGetProperty("lrc", out var lrc)) continue;
                string text = MusicBrainzClient.Text(lrc, "lyric");
                if (text.Length > 0) lyrics.Add(new(entry.Id, entry.Match.Title, entry.Match.Artist, entry.Match.Album,
                    entry.Duration, "", text, SourceUrl: "https://music.163.com/song?id=" + entry.Id));
            }
            return lyrics;
        }, () => SearchKugou(title, artistHint, token));
}
