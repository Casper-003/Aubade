using System.ComponentModel;
using System.Runtime.CompilerServices;
using Vesper.Core;

namespace Vesper.App;

public sealed class LibraryDirectoryItem(string path)
{
    public string Path { get; } = path;
    public string Name => System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(Path)) is { Length: > 0 } name ? name : Path;
    public string Availability => Directory.Exists(Path) ? "可用" : "目录不可用";
    public string AvailabilityGlyph => Directory.Exists(Path) ? "\uE73E" : "\uE7BA";
}

public sealed class LibrarySongItem(Song song) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private bool favorite;
    private string resolution = "";
    private string artist = "未知艺人";
    private SongDetails details = new();
    private object? cover;
    private bool lookupPending;
    private string displayLyrics = "";
    private SongMetadata? effective;
    private SongMetadata Effective => effective ??= details.Effective(Song.Title);
    private uint coverColor = 0xFF48545C;
    public uint CoverColor { get => coverColor; set { if (coverColor == value) return; coverColor = value; Notify(); } }
    public Song Song { get; private set; } = song;
    public string Title => Effective.Title;
    public string Artist
    {
        get => ArtistDetail.Length > 0 ? ArtistDetail : artist;
        set { if (artist == value) return; artist = value; Notify(); }
    }
    public string ArtistDetail => Effective.Artist;
    public string Album => Effective.Album;
    public string Publisher => Effective.Publisher;
    public string Year => Effective.Year;
    public string Lyrics => displayLyrics;
    public string SourceUrl => Effective.SourceUrl;
    public string CoverUrl => Effective.CoverUrl;
    public double DurationSeconds => details.Local?.DurationSeconds ?? 0;
    public string MatchedVideoUrl => details.Video?.Url ?? "";
    public bool HasMatchedVideo => details.Video is not null;
    public bool HasArtist => ArtistDetail.Length > 0;
    public bool HasAlbum => Album.Length > 0;
    public bool HasPublisher => Publisher.Length > 0;
    public bool HasYear => Year.Length > 0;
    public bool HasLyrics => Lyrics.Length > 0;
    public bool HasNoLyrics => !HasLyrics && !LookupPending;
    public bool HasSource => SourceUrl.Length > 0;
    public string SourceLabel => SourceUrl.Contains("musicbrainz.org", StringComparison.OrdinalIgnoreCase) ? "资料来源：MusicBrainz" : SourceUrl.Contains("music.163.com") ? "资料来源：网易云音乐" : SourceUrl.Contains("deezer.com") ? "资料来源：Deezer" : "资料来源：LRCLIB";
    public string LyricsSourceLabel => LyricsSourceUrl.Contains("music.163.com") ? "歌词来源：网易云音乐" : LyricsSourceUrl.Contains("kugou.com") ? "歌词来源：酷狗音乐" : "歌词来源：LRCLIB";
    public string LyricsSourceUrl => details.OnlineLyrics?.SourceUrl ?? "";
    public bool HasLyricsSource => LyricsSourceUrl.Length > 0;
    public bool LookupPending { get => lookupPending; set { if (lookupPending == value) return; lookupPending = value; Notify(); NotifyLookup(); } }
    public string LookupMessage => LookupPending ? ""
        : details.Lookup is { Metadata: LookupOutcome.NeedsChoice or LookupOutcome.NotFound } ? "歌曲资料未能自动匹配，可手动检索或修改。"
        : details.Lookup is { Metadata: LookupOutcome.Unavailable } ? "资料服务暂不可用，稍后打开时会重试。"
        : details.Lookup is { Lyrics: LookupOutcome.Unavailable } ? "歌词服务暂不可用，稍后打开时会重试。"
        : details.Lookup is { Lyrics: LookupOutcome.NeedsChoice } ? "歌词存在多个匹配，已保留待处理。"
        : "";
    public bool HasLookupMessage => LookupMessage.Length > 0;
    public bool HasNoDetails => !LookupPending && !HasArtist && !HasAlbum && !HasPublisher && !HasYear;
    public object? Cover { get => cover; set { if (ReferenceEquals(cover, value)) return; cover = value; Notify(); Notify(nameof(HasCover)); Notify(nameof(HasNoCover)); } }
    public bool HasCover => Cover is not null;
    public bool HasNoCover => !HasCover;
    public void ApplyDetails(SongDetails value)
    {
        if (details == value) return;
        var old = Effective;
        string oldLyrics = displayLyrics, oldLyricsSource = LyricsSourceUrl, oldMessage = LookupMessage;
        bool oldHasNoDetails = HasNoDetails;
        var oldVideo = details.Video;
        if (details.Online?.CoverUrl != value.Online?.CoverUrl) { Cover = null; CoverColor = 0xFF48545C; }
        details = value;
        effective = value.Effective(Song.Title);
        if (old.Lyrics != Effective.Lyrics) displayLyrics = LyricsDisplay.Clean(Effective.Lyrics);
        Changed(old.Title, Title, nameof(Title));
        Changed(old.Artist, ArtistDetail, nameof(ArtistDetail));
        Changed(old.Artist.Length > 0 ? old.Artist : artist, Artist, nameof(Artist));
        Changed(old.Album, Album, nameof(Album)); Changed(old.Publisher, Publisher, nameof(Publisher));
        Changed(old.Year, Year, nameof(Year)); Changed(oldLyrics, Lyrics, nameof(Lyrics));
        Changed(old.SourceUrl, SourceUrl, nameof(SourceUrl)); Changed(old.CoverUrl, CoverUrl, nameof(CoverUrl));
        Changed(old.Artist.Length > 0, HasArtist, nameof(HasArtist)); Changed(old.Album.Length > 0, HasAlbum, nameof(HasAlbum));
        Changed(old.Publisher.Length > 0, HasPublisher, nameof(HasPublisher)); Changed(old.Year.Length > 0, HasYear, nameof(HasYear));
        Changed(oldLyrics.Length > 0, HasLyrics, nameof(HasLyrics));
        Changed(oldLyrics.Length == 0 && !LookupPending, HasNoLyrics, nameof(HasNoLyrics));
        Changed(old.SourceUrl.Length > 0, HasSource, nameof(HasSource)); Changed(oldHasNoDetails, HasNoDetails, nameof(HasNoDetails));
        if (old.SourceUrl != SourceUrl) Notify(nameof(SourceLabel));
        Changed(oldLyricsSource, LyricsSourceUrl, nameof(LyricsSourceUrl));
        Changed(oldLyricsSource.Length > 0, HasLyricsSource, nameof(HasLyricsSource));
        if (oldLyricsSource != LyricsSourceUrl) Notify(nameof(LyricsSourceLabel));
        Changed(old.DurationSeconds, DurationSeconds, nameof(DurationSeconds));
        Changed(oldVideo?.Url ?? "", MatchedVideoUrl, nameof(MatchedVideoUrl));
        Changed(oldVideo is not null, HasMatchedVideo, nameof(HasMatchedVideo));
        Changed(oldMessage, LookupMessage, nameof(LookupMessage));
        Changed(oldMessage.Length > 0, HasLookupMessage, nameof(HasLookupMessage));
    }
    private void Changed<T>(T before, T after, string property)
    { if (!EqualityComparer<T>.Default.Equals(before, after)) Notify(property); }
    private void NotifyLookup() { Notify(nameof(LookupMessage)); Notify(nameof(HasLookupMessage)); Notify(nameof(HasNoDetails)); Notify(nameof(HasNoLyrics)); }
    public string Resolution { get => resolution; set { if (resolution == value) return; resolution = value; Notify(); Notify(nameof(HasResolution)); } }
    public bool HasResolution => Resolution.Length > 0;
    public bool IsAvailable { get; private set; } = song.Sources.Count > 0;
    public bool IsUnavailable => !IsAvailable;
    public double RowOpacity => IsAvailable ? 1 : 0.5;
    public string UnavailableLabel => IsAvailable ? "" : "文件不可用";
    public bool MetadataLoaded { get; set; }
    public bool MetadataLoading { get; set; }
    public DateTimeOffset MetadataRetryAfter { get; set; }
    public bool IsFavorite
    {
        get => favorite;
        set { if (favorite == value) return; favorite = value; Notify(); Notify(nameof(FavoriteGlyph)); Notify(nameof(FavoriteHint)); }
    }
    public string FavoriteGlyph => IsFavorite ? "\uEB52" : "\uEB51";
    public string FavoriteHint => IsFavorite ? "取消收藏" : "收藏";
    public void Update(Song song, bool isFavorite, bool available)
    {
        bool titleChanged = Song.Title != song.Title;
        bool availabilityChanged = IsAvailable != available;
        if (!Song.Sources.SequenceEqual(song.Sources, StringComparer.OrdinalIgnoreCase)) { MetadataLoaded = false; MetadataRetryAfter = default; }
        Song = song;
        if (titleChanged) effective = null;
        IsAvailable = available;
        IsFavorite = isFavorite;
        if (titleChanged) Notify(nameof(Title));
        if (availabilityChanged) { Notify(nameof(IsAvailable)); Notify(nameof(IsUnavailable)); Notify(nameof(RowOpacity)); Notify(nameof(UnavailableLabel)); }
    }
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
