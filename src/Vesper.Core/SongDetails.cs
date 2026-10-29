namespace Vesper.Core;

public sealed record SongMetadata(string Title = "", string Artist = "", string Album = "",
    string Publisher = "", string Year = "", string Lyrics = "", string SourceUrl = "", string CoverUrl = "",
    double DurationSeconds = 0);

public sealed record SongCorrections(string Title, string Artist, string Album, DateTimeOffset ModifiedAt);

// 资料与扫描得到的文件记录分开保存，扫描快照不能覆盖用户刚刚完成的修正。
public sealed record SongDetails(SongMetadata? Local = null, SongMetadata? Online = null,
    SongCorrections? Corrections = null, MusicVideoMatch? Video = null, SongLyrics? OnlineLyrics = null, SongLookupState? Lookup = null)
{
    public SongMetadata Effective(string fallbackTitle)
    {
        var local = Local ?? new();
        var online = Online ?? new();
        static string Text(string? value) => value?.Trim() ?? "";
        static string First(params string?[] values) => values.Select(Text).FirstOrDefault(value => value.Length > 0) ?? "";
        return new(
            First(Corrections?.Title, online.Title, local.Title, fallbackTitle),
            Corrections is { } edit ? Text(edit.Artist) : First(online.Artist, local.Artist),
            Corrections is { } correction ? Text(correction.Album) : First(online.Album, local.Album),
            First(online.Publisher, local.Publisher), First(online.Year, local.Year),
            First(local.Lyrics, OnlineLyrics?.PlainText), Text(online.SourceUrl), Text(online.CoverUrl), local.DurationSeconds);
    }

    public SongDetails Merge(SongDetails other) => new(Local ?? other.Local, Online ?? other.Online,
        Corrections is null || other.Corrections?.ModifiedAt > Corrections.ModifiedAt ? other.Corrections : Corrections,
        Video ?? other.Video, OnlineLyrics ?? other.OnlineLyrics, Lookup ?? other.Lookup);
}
