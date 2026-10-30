using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;

namespace Vesper.App;

public sealed partial class MainWindow
{
    private async Task<object> CheckOverviewFeedbackAsync(List<string> failures)
    {
        var viewer = FindChild<ScrollViewer>(LibrarySongs)!;
        viewer.ChangeView(null, 0, null, true);
        await Task.Delay(80);
        if (browserContentClipGeometry is null || browserContentClipGeometry.CornerRadius.X != 7
            || Math.Abs(browserContentClipGeometry.Size.X - BrowserCard.ActualWidth + 2) > .5
            || !BrowserContentClip.Margin.Equals(new Thickness(1)))
            failures.Add("Page backgrounds must have a rounded clip inside the one-unit card outline.");
        if (LibraryHeroArtwork.Margin.Right != 0 || Math.Abs(LibraryHeroArtwork.ActualWidth - LibraryPage.ActualWidth) > .5
            || LibraryScrollRail.Visibility != Visibility.Visible
            || Math.Abs(LibraryScrollBar.Maximum - viewer.ScrollableHeight) > 1
            || Math.Abs(LibraryScrollBar.ViewportSize - viewer.ViewportHeight) > 1)
            failures.Add("The header must span the page width with the scrollbar overlaid and matching the real viewport.");

        double left = QueueShellChrome.TransformToVisual(Root).TransformPoint(default).X;
        double right = QueueShellChrome.TransformToVisual(Root).TransformPoint(new Point(QueueShellChrome.ActualWidth, 0)).X;
        double browserWidthBeforeHint = BrowserCard.ActualWidth;
        var gapSamples = new List<double[]>();
        void CheckGaps()
        {
            Point shell = QueueShell.TransformToVisual(Root).TransformPoint(default);
            Point browser = BrowserCard.TransformToVisual(Root).TransformPoint(default);
            double[] gaps = [Root.ActualHeight - shell.Y - QueueShell.ActualHeight,
                shell.X - browser.X - BrowserCard.ActualWidth, Root.ActualWidth - shell.X - QueueShell.ActualWidth];
            gapSamples.Add(gaps);
            if (gaps.Any(gap => Math.Abs(gap - 4) > .6)) failures.Add($"Card gaps must stay four units: {string.Join(",", gaps.Select(gap => gap.ToString("F2")))}.");
        }
        CheckGaps();
        AnimateQueueRailHint(true); await Task.Delay(60); Root.UpdateLayout(); CheckGaps();
        await Task.Delay(150); Root.UpdateLayout(); CheckGaps();
        double peekLeft = QueueShellChrome.TransformToVisual(Root).TransformPoint(default).X;
        double peekRight = QueueShellChrome.TransformToVisual(Root).TransformPoint(new Point(QueueShellChrome.ActualWidth, 0)).X;
        if (Math.Abs(right - peekRight) > .5 || Math.Abs(left - peekLeft - 4) > .5
            || Math.Abs(browserWidthBeforeHint - BrowserCard.ActualWidth - 4) > .5)
            failures.Add("Rail hover must expand its left boundary and shrink the browser, keeping the right edge and all gaps fixed.");
        AnimateQueueRailHint(false); await Task.Delay(180); Root.UpdateLayout(); CheckGaps();

        int fontChanges = 0;
        var widths = new HashSet<int>();
        var frameIntervals = new List<double>();
        long lastFrame = 0;
        long fontToken = LibraryTitleBold.RegisterPropertyChangedCallback(TextBlock.FontSizeProperty, (_, _) => fontChanges++);
        void OnCardSize(object sender, SizeChangedEventArgs args) => widths.Add((int)Math.Round(args.NewSize.Width));
        void OnFrame(object? sender, object args)
        {
            long now = Stopwatch.GetTimestamp();
            if (lastFrame != 0) frameIntervals.Add(Stopwatch.GetElapsedTime(lastFrame, now).TotalMilliseconds);
            lastFrame = now;
        }
        BrowserCard.SizeChanged += OnCardSize;
        CompositionTarget.Rendering += OnFrame;
        try
        {
            SetQueueOpen(true); await Task.Delay(280);
            SetQueueOpen(false); await Task.Delay(280);
        }
        finally
        {
            CompositionTarget.Rendering -= OnFrame;
            BrowserCard.SizeChanged -= OnCardSize;
            LibraryTitleBold.UnregisterPropertyChangedCallback(TextBlock.FontSizeProperty, fontToken);
        }
        static bool HasNonidentityTransform(FrameworkElement element) => element.RenderTransform is { } transform
            && (transform.TransformPoint(default) != default(Point) || transform.TransformPoint(new Point(1, 1)) != new Point(1, 1));
        if (widths.Count < 3 || PageArea.Clip is not null
            || HasNonidentityTransform(BrowserCardChrome) || HasNonidentityTransform(QueueShellChrome))
            failures.Add("Cards must continuously reflow without scaled/clipped substitute cards.");

        viewer.ChangeView(null, Math.Min(200, viewer.ScrollableHeight), null, true);
        await Task.Delay(100);
        if (LibraryScrollBar.IndicatorMode != Microsoft.UI.Xaml.Controls.Primitives.ScrollingIndicatorMode.MouseIndicator)
            failures.Add("Scrolling must reveal the full-page scrollbar.");
        await Task.Delay(1250);
        if (LibraryScrollBar.IndicatorMode != Microsoft.UI.Xaml.Controls.Primitives.ScrollingIndicatorMode.None)
            failures.Add("The scrollbar must hide after scrolling stops.");

        var windowSize = AppWindow.Size;
        int shortLists = 0;
        try
        {
            // A large isolated viewport reproduces the user's seven-song, single-page case.
            AppWindow.Resize(new SizeInt32(1600, 1400));
            viewModel.FavoriteResults.Clear();
            for (int i = 0; i < 7; i++) viewModel.FavoriteResults.Add(viewModel.Results[i]);
            ContentFrame.Navigate(typeof(ShellPage), new ShellNavigationRequest("收藏", this));
            await Task.Delay(180); Root.UpdateLayout(); ConfigureOverview(FavoriteSongs);
            await Task.Delay(120);
            if (FavoriteHeroArtwork.Margin.Right != 0 || Math.Abs(FavoriteHeroArtwork.ActualWidth - FavoritesPage.ActualWidth) > .5)
                failures.Add("Favorite header background must align with the page's right edge.");
            if (overviewMotions.TryGetValue(FavoriteSongs, out var favoriteMotion))
            {
                favoriteMotion.Metrics.TryGetScalar("ExpandedTitleScale", out float expandedScale);
                double fitFont = Math.Max(1, Math.Min(FavoriteHeroLayer.Height * .46 / favoriteMotion.TitleHeightPerFontUnit,
                    Math.Max(1, FavoritesPage.ActualWidth - FavoriteTitleMotion.Margin.Left - 32 - 2) / favoriteMotion.TitleWidthPerFontUnit));
                if (Math.Abs(expandedScale - 1) > .001 || Math.Abs(FavoriteTitleBold.FontSize - fitFont) > .1
                    || FavoriteTitleBold.GetType() != typeof(TextBlock))
                    failures.Add("The large title must be native text laid out at its final font size, without upscaling a smaller surface.");
            }
            var favoriteViewer = FindChild<ScrollViewer>(FavoriteSongs)!;
            favoriteViewer.ChangeView(null, 100, null, true);
            await Task.Delay(80);
            if (favoriteViewer.ScrollableHeight > 1 || favoriteViewer.VerticalOffset > 1 || FavoriteScrollRail.Visibility != Visibility.Collapsed)
                failures.Add("Seven songs that fit on one page must not gain blank scrolling or a scrollbar.");
            else shortLists++;
            ContentFrame.Navigate(typeof(ShellPage), new ShellNavigationRequest("曲库", this));
            while (viewModel.Results.Count > 2) viewModel.Results.RemoveAt(viewModel.Results.Count - 1);
            await Task.Delay(180); Root.UpdateLayout(); ConfigureOverview(LibrarySongs);
            await Task.Delay(120);
            viewer.ChangeView(null, 100, null, true); await Task.Delay(80);
            if (viewer.ScrollableHeight > 1 || viewer.VerticalOffset > 1 || LibraryScrollRail.Visibility != Visibility.Collapsed)
                failures.Add("A shortened library must reset stale scrolling and hide its scrollbar.");
            else shortLists++;
        }
        finally { AppWindow.Resize(windowSize); }
        return new
        {
            DistinctAnimatedCardWidths = widths.Count, TitleFontSizeChanges = fontChanges, ShortListsChecked = shortLists,
            GapSamples = gapSamples,
            UiRenderingCallbacks = frameIntervals.Count,
            MaximumUiCallbackIntervalMilliseconds = Math.Round(frameIntervals.DefaultIfEmpty(0).Max(), 2),
            Note = "UI-thread callback timing is diagnostic, not a display FPS measurement."
        };
    }
}
