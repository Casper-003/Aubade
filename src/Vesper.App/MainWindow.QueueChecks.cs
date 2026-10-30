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
                if (QueueCard.Margin.Left != 4 || QueueCard.Margin.Right != 4 || QueueCard.Margin.Top != 0 || QueueCard.Margin.Bottom != 0)
                    failures.Add("Queue card spacing differs from the shared four-unit edge.");
                Point queueOrigin = QueueCard.TransformToVisual(BrowserCards).TransformPoint(default);
                Point browserOrigin = BrowserCard.TransformToVisual(BrowserCards).TransformPoint(default);
                if (Math.Abs(queueOrigin.Y - browserOrigin.Y) > .5 || Math.Abs(QueueCard.ActualHeight - BrowserCard.ActualHeight) > .5
                    || Math.Abs(queueOrigin.X - browserOrigin.X - BrowserCard.ActualWidth - 4) > .5)
                    failures.Add("The visible queue and browser card edges are not aligned at a four-unit gap.");
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
                if (queuePanelOpen || QueueCardHost.Visibility != Visibility.Collapsed) failures.Add($"cycle {cycle}: queue did not close.");
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
