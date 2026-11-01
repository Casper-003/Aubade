using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace Vesper.App;

public sealed partial class MainWindow
{
    private sealed class OverviewScrollbarState(ListView list, ScrollViewer viewer, Grid rail, ScrollBar bar)
    {
        internal readonly ListView List = list;
        internal readonly ScrollViewer Viewer = viewer;
        internal readonly Grid Rail = rail;
        internal readonly ScrollBar Bar = bar;
        internal readonly DispatcherTimer HideTimer = new() { Interval = TimeSpan.FromSeconds(1) };
        internal readonly List<(DependencyProperty Property, long Token)> MetricCallbacks = [];
        internal readonly Dictionary<RoutedEvent, PointerEventHandler> PointerHandlers = [];
        internal bool CanScroll, Hovered, Pressed, Dragging, Scrolling, Syncing;
        internal double LastOffset = viewer.VerticalOffset;
    }

    private readonly Dictionary<ListView, OverviewScrollbarState> overviewScrollbars = [];

    private void ConfigureOverviewScrollbar(ListView list, ScrollViewer viewer)
    {
        if (overviewScrollbars.TryGetValue(list, out var state))
        {
            if (ReferenceEquals(state.Viewer, viewer)) { SyncOverviewScrollbar(state); return; }
            DetachOverviewScrollbar(state);
        }
        var parts = GetOverviewParts(list);
        state = new(list, viewer, parts.ScrollRail, parts.ScrollBar);
        overviewScrollbars[list] = state;
        viewer.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        viewer.SizeChanged += OnOverviewScrollViewportChanged;
        state.Bar.Scroll += OnOverviewBarScroll;
        state.Rail.PointerEntered += OnOverviewScrollRailEntered;
        state.Rail.PointerExited += OnOverviewScrollRailExited;
        state.HideTimer.Tick += OnOverviewScrollbarHideTimer;
        foreach (var property in new[] { ScrollViewer.ExtentHeightProperty, ScrollViewer.ViewportHeightProperty, ScrollViewer.ScrollableHeightProperty })
            state.MetricCallbacks.Add((property, viewer.RegisterPropertyChangedCallback(property, (_, _) => SyncOverviewScrollbar(state))));
        TrackOverviewRailPointer(state, UIElement.PointerPressedEvent, OnOverviewScrollRailPressed);
        TrackOverviewRailPointer(state, UIElement.PointerReleasedEvent, OnOverviewScrollRailReleased);
        TrackOverviewRailPointer(state, UIElement.PointerCanceledEvent, OnOverviewScrollRailReleased);
        TrackOverviewRailPointer(state, UIElement.PointerCaptureLostEvent, OnOverviewScrollRailReleased);
        SyncOverviewScrollbar(state);
    }

    private static void TrackOverviewRailPointer(OverviewScrollbarState state, RoutedEvent routedEvent, PointerEventHandler handler)
    {
        state.PointerHandlers.Add(routedEvent, handler);
        state.Rail.AddHandler(routedEvent, handler, true);
    }

    private OverviewScrollbarState? OverviewScrollbarFor(object? element) => overviewScrollbars.Values.FirstOrDefault(state =>
        ReferenceEquals(element, state.Viewer) || ReferenceEquals(element, state.Bar)
        || ReferenceEquals(element, state.Rail) || ReferenceEquals(element, state.HideTimer));

    private void SyncOverviewScrollbar(OverviewScrollbarState state)
    {
        if (closed || state.Syncing) return;
        state.Syncing = true;
        double maximum = Math.Max(0, state.Viewer.ScrollableHeight);
        bool canScroll = state.Viewer.ViewportHeight > .5 && maximum > .5
            && state.Viewer.ExtentHeight > state.Viewer.ViewportHeight + .5;
        try
        {
            state.CanScroll = canScroll;
            state.Viewer.VerticalScrollMode = canScroll ? ScrollMode.Enabled : ScrollMode.Disabled;
            state.Bar.ViewportSize = Math.Max(0, state.Viewer.ViewportHeight);
            state.Bar.LargeChange = Math.Max(1, state.Viewer.ViewportHeight * .9);
            state.Bar.SmallChange = 48;
            state.Bar.Maximum = canScroll ? maximum : 0;
            if (!state.Dragging) state.Bar.Value = canScroll ? Math.Clamp(state.Viewer.VerticalOffset, 0, maximum) : 0;
            state.Rail.Visibility = canScroll ? Visibility.Visible : Visibility.Collapsed;
            if (!canScroll)
            {
                state.HideTimer.Stop();
                state.Hovered = state.Pressed = state.Dragging = state.Scrolling = false;
                state.Bar.IndicatorMode = ScrollingIndicatorMode.None;
            }
        }
        finally { state.Syncing = false; }
        double offset = canScroll ? Math.Clamp(state.Viewer.VerticalOffset, 0, maximum) : 0;
        if (Math.Abs(offset - state.Viewer.VerticalOffset) > .01) state.Viewer.ChangeView(null, offset, null, true);
    }

    private void OnOverviewScrollViewportChanged(object sender, SizeChangedEventArgs args)
    { if (OverviewScrollbarFor(sender) is { } state) SyncOverviewScrollbar(state); }

    private void UpdateOverviewScrollbarView(object? sender, ScrollViewerViewChangedEventArgs args)
    {
        if (OverviewScrollbarFor(sender) is not { } state) return;
        SyncOverviewScrollbar(state);
        bool moved = Math.Abs(state.LastOffset - state.Viewer.VerticalOffset) > .01;
        state.LastOffset = state.Viewer.VerticalOffset;
        state.Scrolling = args.IsIntermediate;
        if (moved || args.IsIntermediate) ShowOverviewScrollbar(state);
        if (!args.IsIntermediate) ScheduleOverviewScrollbarHide(state);
    }

    private void OnOverviewScrollbarIdle()
    {
        foreach (var state in overviewScrollbars.Values)
        {
            if (!state.Scrolling) continue;
            state.Scrolling = false;
            ScheduleOverviewScrollbarHide(state);
        }
    }

    private void OnOverviewBarScroll(object sender, ScrollEventArgs args)
    {
        if (OverviewScrollbarFor(sender) is not { CanScroll: true, Syncing: false } state) return;
        state.Dragging = args.ScrollEventType == ScrollEventType.ThumbTrack;
        if (args.ScrollEventType == ScrollEventType.EndScroll) state.Pressed = false;
        ShowOverviewScrollbar(state);
        state.Viewer.ChangeView(null, Math.Clamp(args.NewValue, 0, state.Viewer.ScrollableHeight), null, true);
        ScheduleOverviewScrollbarHide(state);
    }

    private void OnOverviewScrollRailEntered(object sender, PointerRoutedEventArgs args)
    {
        if (OverviewScrollbarFor(sender) is not { } state) return;
        state.Hovered = true;
        ShowOverviewScrollbar(state);
    }

    private void OnOverviewScrollRailExited(object sender, PointerRoutedEventArgs args)
    {
        if (OverviewScrollbarFor(sender) is not { } state) return;
        var point = args.GetCurrentPoint(state.Rail).Position;
        if (point.X >= 0 && point.Y >= 0 && point.X <= state.Rail.ActualWidth && point.Y <= state.Rail.ActualHeight) return;
        state.Hovered = false;
        ScheduleOverviewScrollbarHide(state);
    }

    private void OnOverviewScrollRailPressed(object sender, PointerRoutedEventArgs args)
    {
        if (OverviewScrollbarFor(sender) is not { } state) return;
        state.Pressed = true;
        ShowOverviewScrollbar(state);
    }

    private void OnOverviewScrollRailReleased(object sender, PointerRoutedEventArgs args)
    {
        if (OverviewScrollbarFor(sender) is not { } state) return;
        state.Pressed = state.Dragging = false;
        ScheduleOverviewScrollbarHide(state);
    }

    private static void ShowOverviewScrollbar(OverviewScrollbarState state)
    {
        if (!state.CanScroll) return;
        state.HideTimer.Stop();
        state.Bar.IndicatorMode = ScrollingIndicatorMode.MouseIndicator;
    }

    private static void ScheduleOverviewScrollbarHide(OverviewScrollbarState state)
    {
        state.HideTimer.Stop();
        if (state.CanScroll && !state.Hovered && !state.Pressed && !state.Dragging && !state.Scrolling) state.HideTimer.Start();
    }

    private void OnOverviewScrollbarHideTimer(object? sender, object args)
    {
        if (OverviewScrollbarFor(sender) is not { } state) return;
        state.HideTimer.Stop();
        if (!closed && !state.Hovered && !state.Pressed && !state.Dragging && !state.Scrolling)
            state.Bar.IndicatorMode = ScrollingIndicatorMode.None;
    }

    private void DetachOverviewScrollbar(OverviewScrollbarState state)
    {
        state.HideTimer.Stop();
        state.HideTimer.Tick -= OnOverviewScrollbarHideTimer;
        state.Viewer.SizeChanged -= OnOverviewScrollViewportChanged;
        state.Bar.Scroll -= OnOverviewBarScroll;
        state.Rail.PointerEntered -= OnOverviewScrollRailEntered;
        state.Rail.PointerExited -= OnOverviewScrollRailExited;
        foreach (var (property, token) in state.MetricCallbacks) state.Viewer.UnregisterPropertyChangedCallback(property, token);
        foreach (var (routedEvent, handler) in state.PointerHandlers) state.Rail.RemoveHandler(routedEvent, handler);
    }

    private void CloseOverviewScrollbars()
    {
        foreach (var state in overviewScrollbars.Values) DetachOverviewScrollbar(state);
        overviewScrollbars.Clear();
    }
}
