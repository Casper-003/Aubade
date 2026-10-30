using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Vesper.Core;
using Windows.Foundation;
using Windows.ApplicationModel.DataTransfer;
using Microsoft.UI.Xaml.Media;

namespace Vesper.App;

public sealed partial class MainWindow
{
    // Opt-in regression for a native fail-fast. It never initializes or writes the user's library.
    private async Task RunQueueRuntimeChecksAsync(string outputPath)
    {
        var failures = new List<string>();
        int transitions = 0, idleTitleResets = 0, marqueeResets = 0, dragPayloadChecks = 0;
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
                OnQueueToggle(QueueOpenButton, new RoutedEventArgs()); transitions++;
                await Task.Delay(300); Root.UpdateLayout();
                if (!queuePanelOpen || QueueCardHost.ActualWidth < 100) failures.Add($"cycle {cycle}: queue did not open.");
                if (QueueShell.Margin.Left != 4 || QueueShell.Margin.Right != 0 || QueueShell.Margin.Top != 0 || QueueShell.Margin.Bottom != 0)
                    failures.Add("Queue card spacing differs from the shared four-unit edge.");
                Point queueOrigin = QueueShell.TransformToVisual(BrowserCards).TransformPoint(default);
                Point browserOrigin = BrowserCard.TransformToVisual(BrowserCards).TransformPoint(default);
                if (Math.Abs(queueOrigin.Y - browserOrigin.Y) > .5 || Math.Abs(QueueShell.ActualHeight - BrowserCard.ActualHeight) > .5
                    || Math.Abs(queueOrigin.X - browserOrigin.X - BrowserCard.ActualWidth - 4) > .5)
                    failures.Add("The visible queue and browser card edges are not aligned at a four-unit gap.");
                if (!ReferenceEquals(BrowserCard.Style, QueueShell.Style) || QueueShell.Shadow is not null || QueueCard.Shadow is not null
                    || !BrowserCard.BorderThickness.Equals(new Thickness(1)) || !BrowserCard.BorderThickness.Equals(QueueShell.BorderThickness))
                    failures.Add("Each card must independently draw the same one-unit light outline.");
                Point arrowCenter = QueueToggleChevron.TransformToVisual(QueueShell).TransformPoint(new Point(QueueToggleChevron.ActualWidth / 2, QueueToggleChevron.ActualHeight / 2));
                if (Math.Abs(arrowCenter.Y - QueueShell.ActualHeight / 2) > .5
                    || Math.Abs(arrowCenter.X - QueueShell.BorderThickness.Left - QueueOpenButton.ActualWidth / 2) > .5
                    || QueueToggleChevron.Glyph != "\uE76C" || QueueBadge.Visibility != Visibility.Collapsed)
                    failures.Add("Expanded queue must have a centered left-edge chevron and a single heading count.");
                if (QueueSongs.ItemsPanelRoot is not ItemsStackPanel panel) throw new InvalidOperationException("Queue list did not realize its virtualizing panel.");
                foreach (var container in panel.Children.OfType<ListViewItem>())
                {
                    if (container.ContentTemplateRoot is not FrameworkElement row || row.FindName("QueueTitleViewport") is not Grid host
                        || host.Children.OfType<TextBlock>().FirstOrDefault() is not { } title) continue;
                    if (dragPayloadChecks <= cycle && row.DataContext is QueueSongItem item)
                    {
                        if (row.FindName("QueueDragHandle") is not ContentControl { IsTabStop: true }) failures.Add("Drag handle must be a focusable non-button surface.");
                        var package = new DataPackage(); PrepareQueueDrag(item, package);
                        if (!package.GetView().Contains(QueueDragFormat) || package.RequestedOperation != DataPackageOperation.Move
                            || (string)await package.GetView().GetDataAsync(QueueDragFormat) != item.Entry.Id.ToString("D"))
                            failures.Add("Queue drag package lacks its move operation or entry payload.");
                        dragPayloadChecks++; ClearQueueDrag();
                    }
                    ResetQueueTitle(host); ResetQueueTitle(host); idleTitleResets += 2;
                    title.Text = string.Concat(Enumerable.Repeat("超长队列标题复现 ", 14));
                    title.UpdateLayout();
                    StartQueueTitleMarquee(host);
                    if (!queueScrollingTitles.Contains(title)) failures.Add($"cycle {cycle}: long title animation was not started.");
                    ResetQueueTitle(host); ResetQueueTitle(host); marqueeResets++;
                }
                OnQueueToggle(QueueOpenButton, new RoutedEventArgs()); transitions++;
                await Task.Delay(280); Root.UpdateLayout();
                if (queuePanelOpen || QueueCardHost.Visibility != Visibility.Collapsed || QueueToggleChevron.Glyph != "\uE76B"
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
                    MarqueeResets = marqueeResets, DragPayloadChecks = dragPayloadChecks, Failures = failures,
                    Limitations = "Isolated synthetic controls and direct invocation of the real queue toggle handler; no user library, media decoding, physical mouse timing or visual acceptance."
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            finally { if (File.Exists(probe)) File.Delete(probe); Close(); }
        }
    }
}
