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
    public Song Song { get; private set; } = song;
    public string Title => Song.Title;
    public string Artist { get => artist; set { if (artist == value) return; artist = value; Notify(); } }
    public string Resolution { get => resolution; set { if (resolution == value) return; resolution = value; Notify(); Notify(nameof(HasResolution)); } }
    public bool HasResolution => Resolution.Length > 0;
    public bool IsAvailable { get; private set; } = song.Sources.Count > 0;
    public bool IsUnavailable => !IsAvailable;
    public double RowOpacity => IsAvailable ? 1 : 0.5;
    public string UnavailableLabel => IsAvailable ? "" : "文件不可用";
    public bool MetadataLoaded { get; set; }
    public bool MetadataLoading { get; set; }
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
        if (!Song.Sources.SequenceEqual(song.Sources, StringComparer.OrdinalIgnoreCase)) MetadataLoaded = false;
        Song = song;
        IsAvailable = available;
        IsFavorite = isFavorite;
        if (titleChanged) Notify(nameof(Title));
        if (availabilityChanged) { Notify(nameof(IsAvailable)); Notify(nameof(IsUnavailable)); Notify(nameof(RowOpacity)); Notify(nameof(UnavailableLabel)); }
    }
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
