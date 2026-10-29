using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Vesper.App;

public sealed record ActivitySnapshot(bool IsBusy, string Label, string Tooltip, string Glyph);

// 后台可频繁报告进度，界面只展示最新快照，每个已展示状态至少保持一秒。
public sealed class ActivityStatusPresenter : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private ActivitySnapshot current = new(false, "就绪", "没有后台活动", "\uE73E");
    private ActivitySnapshot? pending;
    private TimeSpan changedAt = -TimeSpan.FromSeconds(1);
    private bool isPaneOpen = true;
    public TimeSpan MinimumDuration { get; } = TimeSpan.FromSeconds(1);
    public bool IsBusy => current.IsBusy;
    public bool Idle => !current.IsBusy;
    public string Label => current.Label;
    public string Tooltip => current.Tooltip;
    public string Glyph => current.Glyph;
    public bool IsPaneOpen
    {
        get => isPaneOpen;
        set { if (isPaneOpen == value) return; isPaneOpen = value; Notify(); }
    }

    public bool Request(ActivitySnapshot latest, TimeSpan now)
    {
        pending = latest == current ? null : latest;
        return Flush(now);
    }

    public bool Flush(TimeSpan now)
    {
        if (pending is null || now - changedAt < MinimumDuration) return false;
        current = pending;
        pending = null;
        changedAt = now;
        foreach (string property in new[] { nameof(IsBusy), nameof(Idle), nameof(Label), nameof(Tooltip), nameof(Glyph) }) Notify(property);
        return true;
    }
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
