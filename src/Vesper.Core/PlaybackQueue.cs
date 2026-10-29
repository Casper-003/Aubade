using System.Collections.ObjectModel;

namespace Vesper.Core;

public sealed record QueueEntry(Guid Id, string SongId, string Title);
public enum PlaybackState { Idle, Loading, Playing, Paused }

public sealed class PlaybackQueue
{
    public ObservableCollection<QueueEntry> Pending { get; } = [];
    public QueueEntry? Current { get; private set; }
    public PlaybackState State { get; private set; }

    public QueueEntry Add(Song song)
    {
        var entry = new QueueEntry(Guid.NewGuid(), song.Id, song.Title);
        Pending.Add(entry);
        return entry;
    }

    public Song? StartNext(IEnumerable<Song> library, out int skipped)
    {
        Current = null;
        State = PlaybackState.Idle;
        skipped = 0;
        var lookup = library.ToDictionary(s => s.Id);
        while (Pending.Count > 0)
        {
            QueueEntry entry = Pending[0];
            Pending.RemoveAt(0);
            if (!lookup.TryGetValue(entry.SongId, out Song? song) || !song.IsAvailable)
            { skipped++; continue; }
            Current = entry;
            State = PlaybackState.Loading;
            return song;
        }
        return null;
    }

    public void MarkPlaying() { if (Current is not null) State = PlaybackState.Playing; }
    public void MarkPaused() { if (Current is not null) State = PlaybackState.Paused; }

    public void RemapSongIds(IReadOnlyDictionary<string, string> aliases)
    {
        for (int i = 0; i < Pending.Count; i++)
            if (aliases.TryGetValue(Pending[i].SongId, out string? id)) Pending[i] = Pending[i] with { SongId = id };
        if (Current is not null && aliases.TryGetValue(Current.SongId, out string? currentId))
            Current = Current with { SongId = currentId };
    }

    public void ExitSession()
    {
        if (Current is not null) Pending.Insert(0, Current);
        Current = null;
        State = PlaybackState.Idle;
    }
}
