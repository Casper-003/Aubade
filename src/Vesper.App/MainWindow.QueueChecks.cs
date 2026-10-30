using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Vesper.Core;
using Windows.Foundation;

namespace Vesper.App;

public sealed partial class MainWindow
{
    // Opt-in regression for a native fail-fast. It never initializes or writes the user's library.
    private async Task RunQueueRuntimeChecksAsync(string outputPath)
    {
        var failures = new List<string>();
        int transitions = 0, idleTitleResets = 0, marqueeResets = 0;
        string probe = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outputPath))!, $"queue-probe-{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(probe)!);
            await File.WriteAllTextAsync(probe, "isolated queue source");
            for (int i = 0; i < 9; i++)
                viewModel.Queue.Add(new($"queue-check-{i}", i % 2 == 0 ? "普通标题" : string.Concat(Enumerable.Repeat("需要滚动展示的超长队列标题 ", 12)), [probe]));
            await Task.Delay(150);
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
                    || !BrowserCard.BorderThickness.Equals(QueueShell.BorderThickness))
                    failures.Add("Left and right cards must share the same light edge treatment.");
                Point arrowCenter = QueueToggleChevron.TransformToVisual(QueueShell).TransformPoint(new Point(QueueToggleChevron.ActualWidth / 2, QueueToggleChevron.ActualHeight / 2));
                if (Math.Abs(arrowCenter.Y - QueueShell.ActualHeight / 2) > .5 || QueueToggleChevron.Glyph != "\uE76C")
                    failures.Add($"Expanded queue chevron must point right at the sidebar's vertical center: actual={arrowCenter.Y:F3}, expected={QueueShell.ActualHeight / 2:F3}, glyph={(int)QueueToggleChevron.Glyph[0]:X4}.");
                if (QueueSongs.ItemsPanelRoot is not ItemsStackPanel panel) throw new InvalidOperationException("Queue list did not realize its virtualizing panel.");
                foreach (var container in panel.Children.OfType<ListViewItem>())
                {
                    if (container.ContentTemplateRoot is not FrameworkElement row || row.FindName("QueueTitleViewport") is not Grid host
                        || host.Children.OfType<TextBlock>().FirstOrDefault() is not { } title) continue;
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
                    || Math.Abs(QueueShell.ActualWidth - 36) > .5) failures.Add($"cycle {cycle}: queue did not close to its single sidebar.");
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
                    MarqueeResets = marqueeResets, Failures = failures,
                    Limitations = "Isolated synthetic controls and direct invocation of the real queue toggle handler; no user library, media decoding, physical mouse timing or visual acceptance."
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            finally { if (File.Exists(probe)) File.Delete(probe); Close(); }
        }
    }
}
