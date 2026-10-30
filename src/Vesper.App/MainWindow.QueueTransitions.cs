using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;

namespace Vesper.App;

public sealed partial class MainWindow
{
    private Storyboard? queueLayoutAnimation;
    private bool queueLayoutAnimating;
    private readonly RectangleGeometry queueRevealClip = new();

    private void SetQueueOpen(bool open)
    {
        if (queuePanelOpen == open) return;
        ResetQueueRailHint();
        double from = QueueCardHost.Visibility == Visibility.Visible ? QueueCardHost.ActualWidth : 0;
        if (!open && FindChild<ScrollViewer>(QueueSongs) is { } viewer) queueScrollOffset = viewer.VerticalOffset;
        StopQueueLayoutTransition();
        queuePanelOpen = open;
        UpdateQueueToggle();
        QueueCard.Width = QueueCardWidth;
        queueLayoutAnimating = true;
        QueueCardHost.Width = from;
        QueueCardHost.Visibility = Visibility.Visible;
        PositionQueueCard();
        UpdateQueueHostClip();

        double target = open ? QueueCard.Width : 0;
        var storyboard = new Storyboard();
        var width = new DoubleAnimation
        {
            From = from, To = target, Duration = TimeSpan.FromMilliseconds(220), EnableDependentAnimation = true,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        // The real card boundary drives layout. Native text trimming, action positions,
        // one-unit outlines and corner radii follow it without scaled or clipped stand-ins.
        Storyboard.SetTarget(width, QueueCardHost);
        Storyboard.SetTargetProperty(width, nameof(FrameworkElement.Width));
        storyboard.Children.Add(width);
        storyboard.Completed += (_, _) =>
        {
            if (!ReferenceEquals(queueLayoutAnimation, storyboard)) return;
            // Store the final base value before stopping so stopping cannot expose the
            // starting width and enqueue another intermediate layout.
            QueueCardHost.Width = queuePanelOpen ? QueueCardWidth : 0;
            StopQueueLayoutTransition();
            if (!closed) SettleQueueLayout(restoreScroll: true);
        };
        queueLayoutAnimation = storyboard;
        storyboard.Begin();
        if (open) { queueAvailabilityTimer.Start(); _ = CheckQueueAvailabilityAsync(); }
        else { queueAvailabilityTimer.Stop(); ClearQueueDrag(); }
    }

    private void StopQueueLayoutTransition()
    {
        queueLayoutAnimation?.Stop();
        queueLayoutAnimation = null;
        queueLayoutAnimating = false;
    }

    private void SettleQueueLayout(bool restoreScroll = false)
    {
        QueueCard.Width = QueueCardWidth;
        QueueCardHost.Width = queuePanelOpen ? QueueCard.Width : 0;
        PositionQueueCard();
        UpdateQueueHostClip();
        if (!queuePanelOpen)
        {
            QueueCardHost.Visibility = Visibility.Collapsed;
            QueueOpenButton.Focus(FocusState.Programmatic);
        }
        else if (restoreScroll && FindChild<ScrollViewer>(QueueSongs) is { } scroll) scroll.ChangeView(null, queueScrollOffset, null, true);
    }

    private void OnQueueLayoutChanged(object sender, SizeChangedEventArgs args)
    {
        // Only the user's outer window resize changes this grid's width. The animated
        // Auto column changes its sibling's width, rather than this parent grid.
        if (Math.Abs(args.NewSize.Width - args.PreviousSize.Width) < .5) return;
        if (queueLayoutAnimating) StopQueueLayoutTransition();
        if (queuePanelOpen) SettleQueueLayout();
        else if (QueueCardHost.Visibility == Visibility.Visible) SettleQueueLayout();
    }

    private void OnQueueHostSizeChanged(object sender, SizeChangedEventArgs args)
    {
        // ActualHeight does not notify ElementName bindings. A Canvas also does not
        // stretch its children, so the card must receive the arranged height explicitly.
        PositionQueueCard(args.NewSize.Height);
        UpdateQueueHostClip();
    }

    private void PositionQueueCard(double? hostHeight = null)
    {
        SetQueueCardHeight(hostHeight ?? QueueCardHost.ActualHeight);
        Canvas.SetLeft(QueueCard, Math.Max(0, QueueCardHost.Width) - Math.Max(0, QueueCard.Width));
    }

    private void SetQueueCardHeight(double hostHeight)
    {
        double height = Math.Max(0, hostHeight);
        if (double.IsNaN(QueueCard.Height) || Math.Abs(QueueCard.Height - height) > .01) QueueCard.Height = height;
    }

    private void UpdateQueueHostClip()
    {
        var bounds = new Rect(0, 0, Math.Max(0, QueueCardHost.ActualWidth), Math.Max(0, QueueCardHost.ActualHeight));
        if (!queueRevealClip.Rect.Equals(bounds)) queueRevealClip.Rect = bounds;
        if (!ReferenceEquals(QueueCardHost.Clip, queueRevealClip)) QueueCardHost.Clip = queueRevealClip;
    }
}
