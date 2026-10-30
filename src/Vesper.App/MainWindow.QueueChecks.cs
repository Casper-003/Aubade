using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Vesper.Core;
using Windows.Foundation;
using Microsoft.UI.Xaml.Media;

namespace Vesper.App;

public sealed partial class MainWindow
{
    // Opt-in regression for a native fail-fast. It never initializes or writes the user's library.
    private async Task RunQueueRuntimeChecksAsync(string outputPath)
    {
        var failures = new List<string>();
        int transitions = 0, idleTitleResets = 0, marqueeResets = 0, dragPreviewChecks = 0;
        int browserLayouts = 0, maxLayoutsPerTransition = 0;
        BrowserCard.SizeChanged += (_, _) => browserLayouts++;
        string probe = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outputPath))!, $"queue-probe-{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(probe)!);
            await File.WriteAllTextAsync(probe, "isolated queue source");
            for (int i = 0; i < 9; i++)
                viewModel.Queue.Add(new($"queue-check-{i}", i % 2 == 0 ? "普通标题" : string.Concat(Enumerable.Repeat("需要滚动展示的超长队列标题 ", 12)), [probe]));
            await Task.Delay(150);
            Grid? publicContent = null;
            for (DependencyObject? parent = VisualTreeHelper.GetParent(BrowserCards); parent is not null; parent = VisualTreeHelper.GetParent(parent))
                if (parent is Grid { Name: "ContentGrid" } grid) { publicContent = grid; break; }
            if (publicContent is null || !publicContent.BorderThickness.Equals(new Thickness(0))
                || publicContent.BorderBrush is SolidColorBrush { Color.A: > 0 } || publicContent.Background is SolidColorBrush { Color.A: > 0 })
                failures.Add("The navigation content parent must not draw a shared outline across both cards.");
            for (int cycle = 0; cycle < 8; cycle++)
            {
                if (cycle == 0) { AnimateQueueRailHint(true); AnimateQueueRailContent(.92f); await Task.Delay(160); }
                browserLayouts = 0;
                OnQueueToggle(QueueOpenButton, new RoutedEventArgs()); transitions++;
                if (cycle == 0)
                {
                    await Task.Delay(80);
                    if (queueRevealScale is null || queueRevealScale.ScaleX <= 0 || queueRevealScale.ScaleX >= 1
                        || QueueChevronRotation.Angle <= 0 || QueueChevronRotation.Angle >= 180)
                        failures.Add("Queue reveal and chevron must pass through animated intermediate states.");
                    await Task.Delay(220);
                }
                else await Task.Delay(300);
                Root.UpdateLayout();
                maxLayoutsPerTransition = Math.Max(maxLayoutsPerTransition, browserLayouts);
                if (!queuePanelOpen || QueueCardHost.ActualWidth < 100) failures.Add($"cycle {cycle}: queue did not open.");
                if (QueueShell.Margin.Left != 4 || QueueShell.Margin.Right != 0 || QueueShell.Margin.Top != 0 || QueueShell.Margin.Bottom != 0)
                    failures.Add("Queue card spacing differs from the shared four-unit edge.");
                Point queueOrigin = QueueShell.TransformToVisual(BrowserCards).TransformPoint(default);
                Point browserOrigin = BrowserCard.TransformToVisual(BrowserCards).TransformPoint(default);
                if (Math.Abs(queueOrigin.Y - browserOrigin.Y) > .5 || Math.Abs(QueueShell.ActualHeight - BrowserCard.ActualHeight) > .5
                    || Math.Abs(queueOrigin.X - browserOrigin.X - BrowserCard.ActualWidth - 4) > .5)
                    failures.Add("The visible queue and browser card edges are not aligned at a four-unit gap.");
                if (!ReferenceEquals(BrowserCardChrome.Style, QueueShellChrome.Style) || QueueShellChrome.Shadow is not null || QueueCard.Shadow is not null
                    || !BrowserCardChrome.BorderThickness.Equals(new Thickness(1)) || !BrowserCardChrome.BorderThickness.Equals(QueueShellChrome.BorderThickness))
                    failures.Add("Each card must independently draw the same one-unit light outline.");
                Point arrowCenter = QueueToggleChevron.TransformToVisual(QueueShell).TransformPoint(new Point(QueueToggleChevron.ActualWidth / 2, QueueToggleChevron.ActualHeight / 2));
                if (Math.Abs(arrowCenter.Y - QueueShell.ActualHeight / 2) > .5
                    || Math.Abs(arrowCenter.X - QueueShellChrome.BorderThickness.Left - QueueOpenButton.ActualWidth / 2) > .5
                    || Math.Abs(QueueChevronRotation.Angle - 180) > .5 || QueueBadge.Visibility != Visibility.Collapsed)
                    failures.Add("Expanded queue must have a centered left-edge chevron and a single heading count.");
                if (Math.Abs(QueueOpenButton.ActualHeight - QueueShell.ActualHeight + 2) > .5
                    || QueueSongs.Padding.Right != QueueOpenButton.Width || QueueHeading.Margin.Right != QueueOpenButton.Width
                    || LibrarySongs.Padding.Right < 16 || FavoriteSongs.Padding.Right < 16)
                    failures.Add("The entire rail must be clickable, with a matching right gutter and separate scrollbar space.");
                if (QueueSongs.ItemsPanelRoot is not ItemsStackPanel panel) throw new InvalidOperationException("Queue list did not realize its virtualizing panel.");
                foreach (var container in panel.Children.OfType<ListViewItem>())
                {
                    if (container.ContentTemplateRoot is not FrameworkElement row || row.FindName("QueueTitleViewport") is not Grid host
                        || host.Children.OfType<TextBlock>().FirstOrDefault() is not { } title) continue;
                    if (row.FindName("QueueDragHandle") is not ContentControl { IsTabStop: true }) failures.Add("Drag handle must remain focusable.");
                    if (row is not SongRowSurface surface || row.DataContext is not QueueSongItem model
                        || surface.RowIndex != model.Position - 1 || !ReferenceEquals(surface.HoverGroup, queueHoverGroup))
                        failures.Add("A reused queue row lost its stripe index or separate hover group.");
                    ResetQueueTitle(host); ResetQueueTitle(host); idleTitleResets += 2;
                    title.Text = string.Concat(Enumerable.Repeat("超长队列标题复现 ", 14));
                    title.UpdateLayout();
                    StartQueueTitleMarquee(host);
                    if (!queueScrollingTitles.Contains(title)) failures.Add($"cycle {cycle}: long title animation was not started.");
                    ResetQueueTitle(host); ResetQueueTitle(host); marqueeResets++;
                }
                if (cycle < 3) dragPreviewChecks += await CheckQueueDragPreviewAsync(failures, cycle);
                browserLayouts = 0;
                OnQueueToggle(QueueOpenButton, new RoutedEventArgs()); transitions++;
                await Task.Delay(280); Root.UpdateLayout();
                maxLayoutsPerTransition = Math.Max(maxLayoutsPerTransition, browserLayouts);
                if (queuePanelOpen || QueueCardHost.Visibility != Visibility.Collapsed || Math.Abs(QueueChevronRotation.Angle) > .5
                    || QueueBadge.Visibility != Visibility.Visible || Math.Abs(QueueShell.ActualWidth - QueueSidebarWidth) > .5) failures.Add($"cycle {cycle}: queue did not close to its counted sidebar.");
                if (cycle == 3)
                {
                    viewModel.Queue.StartNext([new("queue-check-0", "当前歌曲", [probe])], out _);
                    viewModel.Queue.MarkPaused();
                }
            }
            // Interrupt both animation directions, then settle on a fully closed card.
            SetQueueOpen(true); transitions++; await Task.Delay(35);
            SetQueueOpen(false); transitions++; await Task.Delay(35);
            SetQueueOpen(true); transitions++; await Task.Delay(280);
            SetQueueOpen(false); transitions++; await Task.Delay(280);
            if (queuePanelOpen || QueueCardHost.Visibility != Visibility.Collapsed) failures.Add("Interrupted transitions did not settle closed.");
            SetQueueOpen(true); transitions++; await Task.Delay(280);
            int pendingBeforeAdd = viewModel.Queue.Pending.Count;
            viewModel.Queue.Add(new("added-while-open", "展开队列时继续加入", [probe]));
            if (!queuePanelOpen || viewModel.Queue.Pending.Count != pendingBeforeAdd + 1
                || QueueBadge.Text != viewModel.Queue.Pending.Count.ToString() || !BrowserCard.IsHitTestVisible || BrowserCards.Children.Count != 2)
                failures.Add("Adding a song must keep the queue open and the browser accessible.");
            SetQueueOpen(false); transitions++; await Task.Delay(280);
            if (idleTitleResets == 0 || marqueeResets == 0) failures.Add("No realized titles exercised the crash path.");
            if (maxLayoutsPerTransition > 2) failures.Add($"Card layout repeated {maxLayoutsPerTransition} times in one transition.");
            if (viewModel.Queue.State != PlaybackState.Paused || viewModel.Queue.Current is null) failures.Add("Queue toggling changed the current song or paused state.");
        }
        catch (Exception error) { failures.Add(error.ToString()); }
        finally
        {
            try
            {
                await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(new
                {
                    Passed = failures.Count == 0, Transitions = transitions, IdleTitleResets = idleTitleResets,
                    MarqueeResets = marqueeResets, DragPreviewChecks = dragPreviewChecks, MaxBrowserLayoutsPerTransition = maxLayoutsPerTransition, Failures = failures,
                    Limitations = "Isolated synthetic controls and real transition/drag-preview methods; no user library, physical pointer gesture, measured FPS or visual acceptance."
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            finally { if (File.Exists(probe)) File.Delete(probe); Close(); }
        }
    }

    private async Task<int> CheckQueueDragPreviewAsync(List<string> failures, int cycle)
    {
        var original = viewModel.Queue.Pending.Select(entry => entry.Id).ToArray();
        QueueSongItem item = queueRows[0];
        if (QueueSongs.ContainerFromItem(item) is not ListViewItem container) throw new InvalidOperationException("No realized source row.");
        Point point = container.TransformToVisual(Root).TransformPoint(new Point(container.ActualWidth - 12, container.ActualHeight / 2));
        if (!StartQueueDragPreview(item, point)) throw new InvalidOperationException("Whole-row preview could not start.");
        Root.UpdateLayout();
        if (queueDragFloating is null || Math.Abs(queueDragFloating.ActualWidth - container.ActualWidth) > 1 || container.Opacity != 0)
            failures.Add("Drag must float the whole row and leave a hidden source placeholder.");
        MoveQueueDragPreview(queueRows.Count - 1); await Task.Delay(30);
        MoveQueueDragPreview(1); Root.UpdateLayout();
        if (!viewModel.Queue.Pending.Select(entry => entry.Id).SequenceEqual(original) || queueRows[1].Entry.Id != item.Entry.Id
            || queueRows.Where((row, index) => row.Position != index + 1).Any())
            failures.Add("Preview must move projected rows and numbers without changing the saved queue.");
        if (cycle == 0)
        {
            CancelQueueDrag();
            if (!queueRows.Select(row => row.Entry.Id).SequenceEqual(original) || queueDragFloating is not null)
                failures.Add("Cancel must restore the original order and remove the floating row.");
        }
        else if (cycle == 1)
        {
            int changes = 0;
            System.Collections.Specialized.NotifyCollectionChangedEventHandler changed = (_, _) => changes++;
            viewModel.Queue.Pending.CollectionChanged += changed;
            try { FinishQueueDrag(true); }
            finally { viewModel.Queue.Pending.CollectionChanged -= changed; }
            if (changes != 1 || viewModel.Queue.Pending[1].Id != item.Entry.Id || queueDragFloating is not null)
                failures.Add("Release must commit one final move for the selected entry.");
        }
        else
        {
            // A concurrent queue mutation must cancel preview without losing the new entry.
            viewModel.Queue.Add(new("queue-concurrent", "拖动时新增", []));
            if (queueDragActive || queueDragFloating is not null || viewModel.Queue.Pending[^1].SongId != "queue-concurrent"
                || !queueRows.Select(row => row.Entry.Id).SequenceEqual(viewModel.Queue.Pending.Select(entry => entry.Id)))
                failures.Add("Concurrent changes must cancel safely and retain the actual queue.");
        }
        await Task.Delay(30); Root.UpdateLayout();
        if (RealizedQueueDragContainers().Any(row => row.Opacity != 1)) failures.Add("Recycled rows retained a hidden drag placeholder.");
        return 1;
    }
}
