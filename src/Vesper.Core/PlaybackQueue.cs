using System.Collections.ObjectModel;
using System.ComponentModel;

namespace Vesper.Core;

public sealed record QueueEntry(Guid Id, string SongId, string Title);
public sealed record QueueRemoval(QueueEntry Entry, int OriginalIndex);
public enum PlaybackState { Idle, Loading, Playing, Paused }

public sealed class PlaybackQueue : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private QueueEntry? current;
    private PlaybackState state;
    public ObservableCollection<QueueEntry> Pending { get; } = [];
    public QueueEntry? Current
    {
        get => current;
        private set
        {
            if (current == value) return;
            current = value;
            PropertyChanged?.Invoke(this, new(nameof(Current)));
        }
    }
    public PlaybackState State
    {
        get => state;
        private set
        {
            if (state == value) return;
            state = value;
            PropertyChanged?.Invoke(this, new(nameof(State)));
        }
    }

    public QueueEntry Add(Song song)
    {
        var entry = new QueueEntry(Guid.NewGuid(), song.Id, song.Title);
        Pending.Add(entry);
        return entry;
    }

    public bool MovePending(Guid entryId, int newIndex)
    {
        int index = FindPending(entryId);
        if (index < 0 || newIndex < 0 || newIndex >= Pending.Count || index == newIndex) return false;
        Pending.Move(index, newIndex);
        return true;
    }

    public bool InsertNext(Guid entryId) => MovePending(entryId, 0);

    public QueueRemoval? RemovePending(Guid entryId)
    {
        int index = FindPending(entryId);
        if (index < 0) return null;
        var removal = new QueueRemoval(Pending[index], index);
        Pending.RemoveAt(index);
        return removal;
    }

    public bool RestorePending(QueueRemoval removal)
    {
        if (Current?.Id == removal.Entry.Id || FindPending(removal.Entry.Id) >= 0) return false;
        Pending.Insert(Math.Clamp(removal.OriginalIndex, 0, Pending.Count), removal.Entry);
        return true;
    }

    private int FindPending(Guid entryId)
    {
        for (int index = 0; index < Pending.Count; index++)
            if (Pending[index].Id == entryId) return index;
        return -1;
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
