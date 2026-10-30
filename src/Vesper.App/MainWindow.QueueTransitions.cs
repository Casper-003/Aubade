using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;

namespace Vesper.App;

public sealed partial class MainWindow
{
    private sealed record QueueTransitionTransform(Transform? Original, CompositeTransform Motion);
    private sealed record QueueTitlePosition(TextBlock Title, double VisibleFontSize, Point Position);
    private readonly Dictionary<FrameworkElement, QueueTransitionTransform> queueTransitionTransforms = [];
    private Storyboard? queueLayoutAnimation;
    private bool queueLayoutAnimating;
    private RectangleGeometry? queueRevealClip;
    private ScaleTransform? queueRevealScale, browserRevealScale;
    private RectangleGeometry? browserRevealClip;
    private RectangleGeometry? queueOriginalPageClip;

    private void SetQueueOpen(bool open)
    {
        if (queuePanelOpen == open) return;
        if (!open && FindChild<ScrollViewer>(QueueSongs) is { } viewer) queueScrollOffset = viewer.VerticalOffset;

        double browserWidth = BrowserCardChrome.ActualWidth * TransitionScaleX(BrowserCardChrome);
        double queueWidth = QueueShellChrome.ActualWidth * TransitionScaleX(QueueShellChrome);
        double visibleQueueWidth = queueRevealClip is not null && queueRevealScale is not null
            ? queueRevealClip.Rect.Width * queueRevealScale.ScaleX
            : QueueCardHost.Visibility == Visibility.Visible ? QueueCardHost.ActualWidth : 0;
        double visiblePageWidth = browserRevealClip is not null && browserRevealScale is not null
            ? browserRevealClip.Rect.Width * browserRevealScale.ScaleX : PageArea.ActualWidth;
        Point railPosition = QueueOpenButton.TransformToVisual(BrowserCards).TransformPoint(default);
        Point contentPosition = QueueCard.TransformToVisual(BrowserCards).TransformPoint(default);
        double contentOpacity = QueueCard.Opacity;
        var titles = new[] { LibraryTitleBold, FavoriteTitleBold }
            .Where(title => title.ActualWidth > 0)
            .Select(title => new QueueTitlePosition(title, QueueVisibleTitleFontSize(title) * TransitionScaleX(title), title.TransformToVisual(BrowserCards).TransformPoint(default))).ToArray();
        var actions = QueueOverviewActions().ToDictionary(element => element, element => element.TransformToVisual(BrowserCards).TransformPoint(default));

        StopQueueLayoutTransition();
        queuePanelOpen = open;
        UpdateQueueToggle();
        queueLayoutAnimating = true;
        QueueCardHost.Visibility = Visibility.Visible;
        QueueCard.Width = QueueCardWidth;
        // Width changes exactly once. Text measurement and ListView layout happen here,
        // while the following transform/clip animations remain independent of layout.
        QueueCardHost.Width = open ? QueueCard.Width : 0;
        PositionQueueCard();
        Root.UpdateLayout();

        var storyboard = new Storyboard();
        AnimateChrome(BrowserCardChrome, browserWidth, false, storyboard);
        AnimateChrome(QueueShellChrome, queueWidth, true, storyboard);
        var rail = TransitionTransform(QueueOpenButton);
        AnimateQueueDouble(storyboard, rail, nameof(CompositeTransform.TranslateX),
            railPosition.X - QueueOpenButton.TransformToVisual(BrowserCards).TransformPoint(default).X, 0);

        double width = Math.Max(1, QueueCard.ActualWidth);
        double right = QueueCardHost.ActualWidth;
        queueRevealScale = new ScaleTransform { CenterX = right, ScaleX = open ? 1 : 0 };
        queueRevealClip = new RectangleGeometry { Rect = new Rect(right - width, 0, width, Math.Max(0, QueueCardHost.ActualHeight)), Transform = queueRevealScale };
        QueueCardHost.Clip = queueRevealClip;
        AnimateQueueDouble(storyboard, queueRevealScale, nameof(ScaleTransform.ScaleX), Math.Clamp(visibleQueueWidth / width, 0, 1), open ? 1 : 0);

        var content = TransitionTransform(QueueCard);
        double contentOffset = visibleQueueWidth > .5
            ? contentPosition.X - QueueCard.TransformToVisual(BrowserCards).TransformPoint(default).X : 12;
        AnimateQueueDouble(storyboard, content, nameof(CompositeTransform.TranslateX), contentOffset, open ? 0 : 8);
        AnimateQueueDouble(storyboard, QueueCard, nameof(UIElement.Opacity), visibleQueueWidth > .5 ? contentOpacity : .82, open ? 1 : .84);

        queueOriginalPageClip = PageArea.Clip;
        browserRevealScale = new ScaleTransform { ScaleX = 1 };
        browserRevealClip = new RectangleGeometry { Rect = new Rect(0, 0, Math.Max(1, PageArea.ActualWidth), Math.Max(0, PageArea.ActualHeight)), Transform = browserRevealScale };
        PageArea.Clip = browserRevealClip;
        AnimateQueueDouble(storyboard, browserRevealScale, nameof(ScaleTransform.ScaleX), Math.Clamp(visiblePageWidth / browserRevealClip.Rect.Width, 0, 1), 1);

        foreach (var snapshot in titles)
        {
            var motion = TransitionTransform(snapshot.Title);
            Point destination = snapshot.Title.TransformToVisual(BrowserCards).TransformPoint(default);
            double scale = snapshot.VisibleFontSize / Math.Max(1, QueueVisibleTitleFontSize(snapshot.Title));
            AnimateQueueDouble(storyboard, motion, nameof(CompositeTransform.ScaleX), scale, 1);
            AnimateQueueDouble(storyboard, motion, nameof(CompositeTransform.ScaleY), scale, 1);
            AnimateQueueDouble(storyboard, motion, nameof(CompositeTransform.TranslateX), snapshot.Position.X - destination.X, 0);
            AnimateQueueDouble(storyboard, motion, nameof(CompositeTransform.TranslateY), snapshot.Position.Y - destination.Y, 0);
        }
        foreach (var (element, position) in actions)
        {
            if (element.ActualWidth <= 0) continue;
            var motion = TransitionTransform(element);
            double destination = element.TransformToVisual(BrowserCards).TransformPoint(default).X;
            AnimateQueueDouble(storyboard, motion, nameof(CompositeTransform.TranslateX), position.X - destination, 0);
        }

        storyboard.Completed += (_, _) =>
        {
            if (!ReferenceEquals(queueLayoutAnimation, storyboard)) return;
            StopQueueLayoutTransition();
            if (closed) return;
            QueueCard.Width = QueueCardWidth;
            QueueCardHost.Width = queuePanelOpen ? QueueCard.Width : 0;
            PositionQueueCard();
            if (!queuePanelOpen)
            {
                QueueCardHost.Visibility = Visibility.Collapsed;
                QueueOpenButton.Focus(FocusState.Programmatic);
            }
            else if (FindChild<ScrollViewer>(QueueSongs) is { } scroll) scroll.ChangeView(null, queueScrollOffset, null, true);
        };
        queueLayoutAnimation = storyboard;
        storyboard.Begin();
        if (open) { queueAvailabilityTimer.Start(); _ = CheckQueueAvailabilityAsync(); }
        else { queueAvailabilityTimer.Stop(); ClearQueueDrag(); }
    }

    private double TransitionScaleX(FrameworkElement element) =>
        queueTransitionTransforms.TryGetValue(element, out var transform) ? transform.Motion.ScaleX : 1;

    private double QueueVisibleTitleFontSize(TextBlock title)
    {
        bool favorite = ReferenceEquals(title, FavoriteTitleBold);
        var list = favorite ? FavoriteSongs : LibrarySongs;
        var hero = favorite ? FavoriteHeroLayer : LibraryHeroLayer;
        var viewer = overviewMotions.TryGetValue(list, out var motion) ? motion.Viewer : FindChild<ScrollViewer>(list);
        double progress = Math.Clamp((viewer?.VerticalOffset ?? 0) / Math.Max(1, hero.Height - 48), 0, 1);
        // The title's parent already scales toward a fixed 24-unit compact title.
        // Compensate the visible size, so a compact title stays 24 during card motion.
        return title.FontSize * (1 - progress) + 24 * progress;
    }

    private CompositeTransform TransitionTransform(FrameworkElement element)
    {
        if (queueTransitionTransforms.TryGetValue(element, out var existing)) return existing.Motion;
        var motion = new CompositeTransform();
        var original = element.RenderTransform;
        if (original is null) element.RenderTransform = motion;
        else
        {
            var group = new TransformGroup();
            group.Children.Add(original); group.Children.Add(motion); element.RenderTransform = group;
        }
        queueTransitionTransforms.Add(element, new(original, motion));
        return motion;
    }

    private void AnimateChrome(FrameworkElement element, double previousWidth, bool rightAnchored, Storyboard storyboard)
    {
        var motion = TransitionTransform(element);
        motion.CenterX = rightAnchored ? element.ActualWidth : 0;
        AnimateQueueDouble(storyboard, motion, nameof(CompositeTransform.ScaleX), previousWidth > 0 ? previousWidth / Math.Max(1, element.ActualWidth) : 1, 1);
    }

    private static void AnimateQueueDouble(Storyboard storyboard, DependencyObject target, string property, double from, double to)
    {
        if (Math.Abs(from - to) < .001) return;
        var animation = new DoubleAnimation
        {
            From = from, To = to, Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        Storyboard.SetTarget(animation, target); Storyboard.SetTargetProperty(animation, property);
        storyboard.Children.Add(animation);
    }

    private IEnumerable<FrameworkElement> QueueOverviewActions()
    {
        if (LibraryActions.Visibility == Visibility.Visible) yield return LibraryActions;
        foreach (var list in new[] { LibrarySongs, FavoriteSongs })
            if (list.ItemsPanelRoot is Panel panel)
                foreach (var container in panel.Children.OfType<ListViewItem>())
                    if (container.ContentTemplateRoot is SongRowSurface surface)
                        foreach (var actions in surface.Children.OfType<FrameworkElement>().Where(child => Grid.GetColumn(child) == 2)) yield return actions;
    }

    private void StopQueueLayoutTransition()
    {
        queueLayoutAnimation?.Stop();
        queueLayoutAnimation = null;
        foreach (var (element, transform) in queueTransitionTransforms) element.RenderTransform = transform.Original;
        queueTransitionTransforms.Clear();
        QueueCard.Opacity = 1;
        if (browserRevealClip is not null) PageArea.Clip = queueOriginalPageClip;
        queueOriginalPageClip = null;
        browserRevealClip = queueRevealClip = null;
        browserRevealScale = queueRevealScale = null;
        queueLayoutAnimating = false;
        UpdateQueueHostClip();
    }

    private void OnQueueLayoutChanged(object sender, SizeChangedEventArgs args)
    {
        if (!queuePanelOpen || queueLayoutAnimating) return;
        QueueCard.Width = QueueCardWidth; QueueCardHost.Width = QueueCard.Width;
        PositionQueueCard();
    }

    private void OnQueueHostSizeChanged(object sender, SizeChangedEventArgs args)
    {
        PositionQueueCard();
        if (!queueLayoutAnimating) UpdateQueueHostClip();
    }

    private void PositionQueueCard() => Canvas.SetLeft(QueueCard, Math.Max(0, QueueCardHost.Width) - Math.Max(0, QueueCard.Width));

    private void UpdateQueueHostClip() => QueueCardHost.Clip = new RectangleGeometry
    {
        Rect = new Rect(0, 0, Math.Max(0, QueueCardHost.ActualWidth), Math.Max(0, QueueCardHost.ActualHeight))
    };
}
