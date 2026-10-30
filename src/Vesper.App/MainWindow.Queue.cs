using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Windowing;
using Vesper.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;

namespace Vesper.App;

public sealed partial class MainWindow
{
    private readonly ObservableCollection<QueueSongItem> queueRows = [];
    private QueueSongItem? currentQueueRow, queueDropRow;
    private readonly Stack<QueueRemoval> queueUndo = [];
    private readonly DispatcherTimer queueUndoTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer queueAdditionTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly DispatcherTimer queueAvailabilityTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer queueDragTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private Storyboard? queueLayoutAnimation;
    private bool queuePanelOpen, queueAvailabilityBusy, queueCloseApproved, queueClosing;
    private int queueAvailabilityRevision;
    private double queueScrollOffset, queueDragScroll;
    private Guid? queueDragId;
    private readonly HashSet<TextBlock> queueScrollingTitles = [];

    private void InitializeQueuePresentation()
    {
        QueueSongs.ItemsSource = queueRows;
        QueueSongs.ContainerContentChanging += OnQueueContainerChanging;
        viewModel.Queue.Pending.CollectionChanged += OnQueueCollectionChanged;
        viewModel.Queue.PropertyChanged += OnQueueStateChanged;
        viewModel.PropertyChanged += OnQueueLibraryChanged;
        queueUndoTimer.Tick += (_, _) => { queueUndoTimer.Stop(); queueUndo.Clear(); QueueUndoToast.Visibility = Visibility.Collapsed; };
        queueAdditionTimer.Tick += (_, _) => { queueAdditionTimer.Stop(); QueueAdditionToast.Visibility = Visibility.Collapsed; };
        queueAvailabilityTimer.Tick += async (_, _) => await CheckQueueAvailabilityAsync();
        queueDragTimer.Tick += (_, _) =>
        {
            if (FindChild<ScrollViewer>(QueueSongs) is { } viewer)
                viewer.ChangeView(null, Math.Clamp(viewer.VerticalOffset + queueDragScroll, 0, viewer.ScrollableHeight), null, true);
        };
        Root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnQueueRootKeyDown), true);
        AppWindow.Closing += OnQueueWindowClosing;
        RefreshQueuePresentation();
    }

    private void OnQueueContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer.ContentTemplateRoot is FrameworkElement row)
        {
            if (row.FindName("QueueTitleViewport") is Grid titleHost) ResetQueueTitle(titleHost);
            if (args.InRecycleQueue && row.DataContext is QueueSongItem recycled) { recycled.ShowActions(false); recycled.SetFocused(false); }
            else row.DataContext = args.Item;
        }
        else if (!args.InRecycleQueue && args.Phase == 0) args.RegisterUpdateCallback(1, OnQueueContainerChanging);
    }

    private async void OnQueueWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (queueCloseApproved) return;
        args.Cancel = true;
        if (queueClosing) return;
        queueClosing = true;
        Root.IsHitTestVisible = false;
        bool saved = await viewModel.FlushQueueAsync();
        Root.IsHitTestVisible = true;
        queueClosing = false;
        if (saved) { queueCloseApproved = true; Close(); }
    }

    private void CloseQueuePresentation()
    {
        queueLayoutAnimation?.Stop();
        queueUndoTimer.Stop(); queueAdditionTimer.Stop(); queueAvailabilityTimer.Stop(); queueDragTimer.Stop();
        viewModel.Queue.Pending.CollectionChanged -= OnQueueCollectionChanged;
        viewModel.Queue.PropertyChanged -= OnQueueStateChanged;
        viewModel.PropertyChanged -= OnQueueLibraryChanged;
        AppWindow.Closing -= OnQueueWindowClosing;
        foreach (var row in queueRows) row.Dispose();
        currentQueueRow?.Dispose();
        queueScrollingTitles.Clear();
    }

    private void OnQueueCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args) => RefreshQueuePresentation();
    private void OnQueueStateChanged(object? sender, PropertyChangedEventArgs args) => RefreshQueuePresentation();
    private void OnQueueLibraryChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MainViewModel.LibraryHero)) RefreshQueuePresentation();
        if (args.PropertyName == nameof(MainViewModel.Problem))
        {
            QueueSaveWarning.IsOpen = viewModel.Problem.StartsWith("待播队列保存失败", StringComparison.Ordinal);
            QueueSaveWarning.Message = viewModel.Problem;
        }
    }

    private void RefreshQueuePresentation()
    {
        if (closed) return;
        queueAvailabilityRevision++;
        var pending = viewModel.Queue.Pending;
        var ids = pending.Select(entry => entry.Id).ToHashSet();
        for (int i = queueRows.Count - 1; i >= 0; i--)
            if (!ids.Contains(queueRows[i].Entry.Id)) { queueRows[i].Dispose(); queueRows.RemoveAt(i); }
        for (int i = 0; i < pending.Count; i++)
        {
            var item = queueRows.FirstOrDefault(row => row.Entry.Id == pending[i].Id);
            if (item is null) { item = new(pending[i]); queueRows.Insert(i, item); }
            else if (queueRows.IndexOf(item) != i) queueRows.Move(queueRows.IndexOf(item), i);
            item.Update(pending[i], viewModel.FindSong(pending[i].SongId), i + 1);
        }
        if (viewModel.Queue.Current is { } current)
        {
            if (currentQueueRow?.Entry.Id != current.Id) { currentQueueRow?.Dispose(); currentQueueRow = new(current); QueueCurrentContent.Content = currentQueueRow; }
            currentQueueRow.Update(current, viewModel.FindSong(current.SongId), 0);
        }
        else { currentQueueRow?.Dispose(); currentQueueRow = null; QueueCurrentContent.Content = null; }
        QueueCurrentArea.Visibility = currentQueueRow is null ? Visibility.Collapsed : Visibility.Visible;
        QueueCurrentState.Text = viewModel.Queue.State switch { PlaybackState.Playing => "播放中", PlaybackState.Paused => "已暂停", PlaybackState.Loading => "正在准备", _ => "" };
        QueueHeadingCount.Text = pending.Count.ToString(); QueueBadge.Text = pending.Count.ToString();
        UpdateQueueToggle();
        QueueEmpty.Visibility = pending.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        QueueEmptyTitle.Text = currentQueueRow is null ? "还没有待播歌曲" : "后面还没有待播歌曲";
        if (queuePanelOpen) _ = CheckQueueAvailabilityAsync();
    }

    private async Task CheckQueueAvailabilityAsync()
    {
        if (closed || queueAvailabilityBusy) return;
        queueAvailabilityBusy = true;
        int revision = queueAvailabilityRevision;
        var snapshot = queueRows.Concat(currentQueueRow is null ? [] : new[] { currentQueueRow })
            .Select(row => (Row: row, Sources: row.Song?.Song.Sources.ToArray() ?? [])).ToArray();
        try
        {
            bool[] available = await Task.Run(() => snapshot.Select(item => item.Sources.Any(File.Exists)).ToArray());
            if (closed || revision != queueAvailabilityRevision) return;
            for (int i = 0; i < snapshot.Length; i++) snapshot[i].Row.SetAvailable(available[i]);
        }
        finally { queueAvailabilityBusy = false; }
    }

    private void OnQueueToggle(object sender, RoutedEventArgs args) => SetQueueOpen(!queuePanelOpen);
    private void OnQueueClose(object sender, RoutedEventArgs args) => SetQueueOpen(false);
    private void OnQueueRootKeyDown(object sender, KeyRoutedEventArgs args)
    { if (queuePanelOpen && args.Key == VirtualKey.Escape) { SetQueueOpen(false); args.Handled = true; } }
    private double QueueCardGutter => QueueShell.Margin.Left + QueueShell.Margin.Right;
    private double QueueCardWidth => Math.Clamp(BrowserCards.ActualWidth - 36 - 320 - QueueCardGutter, 264, 400);

    private void SetQueueOpen(bool open)
    {
        if (queuePanelOpen == open) return;
        queuePanelOpen = open;
        if (!open && FindChild<ScrollViewer>(QueueSongs) is { } viewer) queueScrollOffset = viewer.VerticalOffset;
        UpdateQueueToggle();
        double from = QueueCardHost.ActualWidth;
        queueLayoutAnimation?.Stop();
        QueueCardHost.Width = from;
        QueueCardHost.Visibility = Visibility.Visible;
        QueueCard.Width = QueueCardWidth;
        double target = open ? QueueCard.Width : 0;
        var storyboard = new Storyboard();
        var animation = new DoubleAnimation { From = from, To = target, Duration = TimeSpan.FromMilliseconds(220), EnableDependentAnimation = true,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } };
        Storyboard.SetTarget(animation, QueueCardHost); Storyboard.SetTargetProperty(animation, "Width"); storyboard.Children.Add(animation);
        storyboard.Completed += (_, _) =>
        {
            if (!ReferenceEquals(queueLayoutAnimation, storyboard)) return;
            storyboard.Stop(); queueLayoutAnimation = null;
            if (queuePanelOpen) QueueCard.Width = QueueCardWidth;
            QueueCardHost.Width = queuePanelOpen ? QueueCard.Width : 0;
            if (!queuePanelOpen) { QueueCardHost.Visibility = Visibility.Collapsed; QueueOpenButton.Focus(FocusState.Programmatic); }
            else if (FindChild<ScrollViewer>(QueueSongs) is { } scroll) scroll.ChangeView(null, queueScrollOffset, null, true);
        };
        queueLayoutAnimation = storyboard; storyboard.Begin();
        if (open) { queueAvailabilityTimer.Start(); _ = CheckQueueAvailabilityAsync(); QueueCloseButton.Focus(FocusState.Programmatic); }
        else { queueAvailabilityTimer.Stop(); ClearQueueDrag(); }
    }

    private void OnQueueLayoutChanged(object sender, SizeChangedEventArgs args)
    {
        if (!queuePanelOpen || queueLayoutAnimation is not null) return;
        QueueCard.Width = QueueCardWidth; QueueCardHost.Width = QueueCard.Width;
    }
    private void OnQueueHostSizeChanged(object sender, SizeChangedEventArgs args) =>
        QueueCardHost.Clip = new RectangleGeometry { Rect = new Rect(0, 0, Math.Max(0, args.NewSize.Width), Math.Max(0, args.NewSize.Height)) };

    private void UpdateQueueToggle()
    {
        QueueToggleChevron.Glyph = queuePanelOpen ? "\uE76C" : "\uE76B";
        string action = queuePanelOpen ? "收起待播曲目" : "展开待播曲目";
        ToolTipService.SetToolTip(QueueOpenButton, $"{action} · {viewModel.Queue.Pending.Count} 首");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(QueueOpenButton, action);
    }

    private async void OnQueueRowLoaded(object sender, RoutedEventArgs args)
    { if (sender is FrameworkElement { DataContext: QueueSongItem { Song: { } song } }) await LoadCoverAsync(song); }
    private void OnQueueRowEntered(object sender, PointerRoutedEventArgs args) { if (sender is FrameworkElement { DataContext: QueueSongItem row }) row.ShowActions(true); }
    private void OnQueueRowExited(object sender, PointerRoutedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: QueueSongItem row } element) return;
        var point = args.GetCurrentPoint(element).Position;
        if (point.X < 0 || point.Y < 0 || point.X > element.ActualWidth || point.Y > element.ActualHeight) row.ShowActions(false);
    }
    private void OnQueueRowFocused(object sender, RoutedEventArgs args) { if (sender is FrameworkElement { DataContext: QueueSongItem row }) row.SetFocused(true); }
    private void OnQueueRowUnfocused(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: QueueSongItem row } element) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (closed) return;
            bool inside = false;
            for (DependencyObject? focus = FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject; focus is not null; focus = VisualTreeHelper.GetParent(focus))
                if (ReferenceEquals(focus, element)) { inside = true; break; }
            row.SetFocused(inside);
        });
    }
    private void OnQueueOpenDetails(object sender, RoutedEventArgs args) { if (sender is FrameworkElement { DataContext: QueueSongItem row }) OpenQueuedDetails(row); }
    private void OnQueueTitleTapped(object sender, TappedRoutedEventArgs args) { if (sender is FrameworkElement { DataContext: QueueSongItem row }) OpenQueuedDetails(row); args.Handled = true; }
    private void OpenQueuedDetails(QueueSongItem row)
    {
        if (row.Song is null) return;
        SetQueueOpen(false);
        ContentFrame.Navigate(typeof(ShellPage), new ShellNavigationRequest("歌曲详情:" + row.Song.Song.Id, this, row.Song.Song.Id), new EntranceNavigationTransitionInfo());
    }
    private void OnQueueRemove(object sender, RoutedEventArgs args) { if (sender is FrameworkElement { DataContext: QueueSongItem row }) RemoveQueued(row); }
    private void RemoveQueued(QueueSongItem row)
    {
        if (viewModel.Queue.RemovePending(row.Entry.Id) is not { } removal) return;
        queueUndo.Push(removal); QueueUndoToast.Visibility = Visibility.Visible;
        queueUndoTimer.Stop(); queueUndoTimer.Start();
    }
    private void OnQueueUndo(object sender, RoutedEventArgs args)
    {
        if (queueUndo.TryPop(out var removal))
        {
            string songId = viewModel.FindSong(removal.Entry.SongId)?.Song.Id ?? removal.Entry.SongId;
            viewModel.Queue.RestorePending(removal with { Entry = removal.Entry with { SongId = songId } });
        }
        queueUndoTimer.Stop();
        if (queueUndo.Count == 0) QueueUndoToast.Visibility = Visibility.Collapsed; else queueUndoTimer.Start();
    }
    private void OnQueueContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: QueueSongItem { IsPending: true } row } element) return;
        var menu = new MenuFlyout();
        var insert = new MenuFlyoutItem { Text = "插播", Icon = new FontIcon { Glyph = "\uE8EF" }, IsEnabled = row.IsAvailable && row.Position > 1 };
        insert.Click += (_, _) => viewModel.Queue.InsertNext(row.Entry.Id);
        var remove = new MenuFlyoutItem { Text = "移除", Icon = new FontIcon { Glyph = "\uE738" } };
        remove.Click += (_, _) => RemoveQueued(row); menu.Items.Add(insert); menu.Items.Add(remove);
        if (args.TryGetPosition(element, out var point)) menu.ShowAt(element, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = point });
        else menu.ShowAt(element);
        args.Handled = true;
    }

    private void OnQueueDragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: QueueSongItem row } || !row.IsPending) { args.Cancel = true; return; }
        queueDragId = row.Entry.Id; args.Data.Properties["VesperQueueEntry"] = row.Entry.Id.ToString(); args.AllowedOperations = DataPackageOperation.Move;
    }
    private bool IsQueueDrag(DragEventArgs args) => queueDragId is not null && args.DataView.Properties.ContainsKey("VesperQueueEntry");
    private void OnQueueDragCompleted(UIElement sender, DropCompletedEventArgs args) => ClearQueueDrag();
    private void ClearQueueDrag() { queueDragId = null; queueDragTimer.Stop(); queueDropRow?.SetDrop(false, false); queueDropRow = null; }
    private void OnQueueRowDragLeave(object sender, DragEventArgs args) { if (sender is FrameworkElement { DataContext: QueueSongItem row }) row.SetDrop(false, false); }
    private void OnQueueRowDragOver(object sender, DragEventArgs args)
    {
        if (!IsQueueDrag(args) || sender is not FrameworkElement { DataContext: QueueSongItem { IsPending: true } row } element) return;
        if (!ReferenceEquals(queueDropRow, row)) queueDropRow?.SetDrop(false, false);
        queueDropRow = row;
        bool before = args.GetPosition(element).Y < element.ActualHeight / 2;
        row.SetDrop(before, !before); AcceptQueueDrag(args); args.Handled = true;
    }
    private void AcceptQueueDrag(DragEventArgs args)
    {
        args.AcceptedOperation = DataPackageOperation.Move;
        double y = args.GetPosition(QueueSongs).Y;
        queueDragScroll = y < 40 ? -18 : y > QueueSongs.ActualHeight - 40 ? 18 : 0;
        if (queueDragScroll == 0) queueDragTimer.Stop(); else queueDragTimer.Start();
    }
    private void OnQueueListDragOver(object sender, DragEventArgs args) { if (IsQueueDrag(args)) AcceptQueueDrag(args); }
    private void OnQueueRowDrop(object sender, DragEventArgs args)
    {
        if (!IsQueueDrag(args) || sender is not FrameworkElement { DataContext: QueueSongItem { IsPending: true } row } element) return;
        int boundary = row.Position - 1 + (args.GetPosition(element).Y >= element.ActualHeight / 2 ? 1 : 0);
        int from = queueRows.ToList().FindIndex(item => item.Entry.Id == queueDragId);
        if (from >= 0) viewModel.Queue.MovePending(queueDragId!.Value, Math.Clamp(boundary - (from < boundary ? 1 : 0), 0, queueRows.Count - 1));
        args.AcceptedOperation = DataPackageOperation.Move; args.Handled = true; ClearQueueDrag();
    }
    private void OnQueueListDrop(object sender, DragEventArgs args)
    { if (IsQueueDrag(args)) { viewModel.Queue.MovePending(queueDragId!.Value, queueRows.Count - 1); args.AcceptedOperation = DataPackageOperation.Move; ClearQueueDrag(); } }
    private void OnQueueHandleKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: QueueSongItem row } || args.Key is not (VirtualKey.Up or VirtualKey.Down)) return;
        viewModel.Queue.MovePending(row.Entry.Id, row.Position - 1 + (args.Key == VirtualKey.Up ? -1 : 1)); args.Handled = true;
    }

    private void OnQueueTitleEntered(object sender, PointerRoutedEventArgs args)
    {
        if (sender is Grid host) StartQueueTitleMarquee(host);
    }
    private void StartQueueTitleMarquee(Grid host)
    {
        if (host.Children.OfType<TextBlock>().FirstOrDefault() is not { } title) return;
        var measure = new TextBlock { Text = title.Text, FontFamily = title.FontFamily, FontSize = title.FontSize, FontWeight = title.FontWeight };
        measure.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double overflow = measure.DesiredSize.Width - host.ActualWidth;
        if (overflow <= 1) return;
        host.Clip = new RectangleGeometry { Rect = new Rect(0, 0, host.ActualWidth, host.ActualHeight) };
        title.Width = measure.DesiredSize.Width; title.TextTrimming = TextTrimming.None;
        ElementCompositionPreview.SetIsTranslationEnabled(title, true);
        var visual = ElementCompositionPreview.GetElementVisual(title);
        visual.Properties.InsertVector3("Translation", Vector3.Zero);
        var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(0, 0); animation.InsertKeyFrame(.15f, 0); animation.InsertKeyFrame(.85f, (float)-overflow); animation.InsertKeyFrame(1, (float)-overflow);
        animation.Duration = TimeSpan.FromSeconds(Math.Max(3, overflow / 30 + 1.5)); animation.IterationBehavior = AnimationIterationBehavior.Forever;
        visual.StartAnimation("Translation.X", animation);
        queueScrollingTitles.Add(title);
    }
    private void OnQueueTitleExited(object sender, PointerRoutedEventArgs args)
    {
        if (sender is Grid host) ResetQueueTitle(host);
    }
    private void ResetQueueTitle(Grid host)
    {
        if (host.Children.OfType<TextBlock>().FirstOrDefault() is not { } title) return;
        // Translation is an opt-in property, not a native Visual property. A newly
        // realized row has no title animation to stop; stopping that path can fail fast.
        if (queueScrollingTitles.Remove(title) && !closed)
        {
            var visual = ElementCompositionPreview.GetElementVisual(title);
            visual.StopAnimation("Translation.X"); visual.Properties.InsertVector3("Translation", Vector3.Zero);
        }
        title.ClearValue(FrameworkElement.WidthProperty); title.TextTrimming = TextTrimming.CharacterEllipsis; host.Clip = null;
    }

    private void OnQueueRowUnloaded(object sender, RoutedEventArgs args)
    { if (sender is FrameworkElement row && row.FindName("QueueTitleViewport") is Grid host) ResetQueueTitle(host); }

    private void ShowQueueAdded(LibrarySongItem song, FrameworkElement? origin)
    {
        QueueAdditionMessage.Text = $"已加入待播 · 第 {viewModel.Queue.Pending.Count} 位 · {song.Title}";
        QueueAdditionToast.Visibility = Visibility.Visible; queueAdditionTimer.Stop(); queueAdditionTimer.Start();
        if (Root.XamlRoot is null || QueueOpenButton.ActualWidth <= 0) return;
        Point start = (origin ?? PageArea).TransformToVisual(Root).TransformPoint(new Point((origin ?? PageArea).ActualWidth / 2, (origin ?? PageArea).ActualHeight / 2));
        Point end = QueueOpenButton.TransformToVisual(Root).TransformPoint(new Point(QueueOpenButton.ActualWidth / 2, QueueOpenButton.ActualHeight / 2));
        var note = new TextBlock { Text = "♪", FontSize = 24, Foreground = (Brush)Navigation.Resources["NavigationViewSelectionIndicatorForeground"] };
        Canvas.SetLeft(note, start.X); Canvas.SetTop(note, start.Y); QueueFlightLayer.Children.Add(note);
        ElementCompositionPreview.SetIsTranslationEnabled(note, true);
        var visual = ElementCompositionPreview.GetElementVisual(note); var compositor = visual.Compositor;
        visual.Properties.InsertVector3("Translation", Vector3.Zero);
        var flight = compositor.CreateVector3KeyFrameAnimation();
        flight.InsertKeyFrame(0, Vector3.Zero); flight.InsertKeyFrame(.5f, new((float)(end.X - start.X) / 2, (float)(end.Y - start.Y) / 2 - 32, 0)); flight.InsertKeyFrame(1, new((float)(end.X - start.X), (float)(end.Y - start.Y), 0)); flight.Duration = TimeSpan.FromMilliseconds(360);
        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        visual.StartAnimation("Translation", flight);
        batch.Completed += (_, _) =>
        {
            QueueFlightLayer.Children.Remove(note);
            if (closed) return;
            var receive = compositor.CreateVector3KeyFrameAnimation(); receive.Target = "Scale";
            receive.InsertKeyFrame(0, Vector3.One); receive.InsertKeyFrame(.5f, new(1.15f, 1.15f, 1)); receive.InsertKeyFrame(1, Vector3.One); receive.Duration = TimeSpan.FromMilliseconds(180);
            QueueOpenButton.StartAnimation(receive);
        };
        batch.End();
    }
}
