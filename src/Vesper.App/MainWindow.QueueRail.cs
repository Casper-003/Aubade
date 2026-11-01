using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;

namespace Vesper.App;

public sealed partial class MainWindow
{
    private Storyboard? queueChevronAnimation;
    private bool queueChevronOpen;

    private void AnimateQueueChevron()
    {
        if (queueChevronOpen == queuePanelOpen) return;
        queueChevronOpen = queuePanelOpen;
        double from = QueueChevronRotation.Angle;
        queueChevronAnimation?.Stop();
        QueueChevronRotation.Angle = from;
        double target = queuePanelOpen ? 180 : 0;
        var storyboard = new Storyboard();
        var turn = new DoubleAnimation
        {
            From = from, To = target, Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(turn, QueueChevronRotation);
        Storyboard.SetTargetProperty(turn, "Angle");
        storyboard.Children.Add(turn);
        storyboard.Completed += (_, _) =>
        {
            if (!ReferenceEquals(queueChevronAnimation, storyboard)) return;
            storyboard.Stop(); QueueChevronRotation.Angle = target; queueChevronAnimation = null;
        };
        queueChevronAnimation = storyboard; storyboard.Begin();
        AnimateQueueRailHint(false);
    }

    private void OnQueueRailEntered(object sender, PointerRoutedEventArgs args)
    { AnimateQueueRailHint(!queuePanelOpen); }
    private void OnQueueRailExited(object sender, PointerRoutedEventArgs args)
    { AnimateQueueRailHint(false); }
    private void OnQueueRailPressed(object sender, PointerRoutedEventArgs args) => AnimateQueueRailContent(.92f);
    private void OnQueueRailReleased(object sender, PointerRoutedEventArgs args) => AnimateQueueRailContent(1);

    private void AnimateQueueRailHint(bool peek)
    {
        AnimateQueueRailPeek(peek);
        AnimateQueueRailContent(1);
    }

    private void ResetQueueRailHint()
    {
        ResetQueueRailPeek();
    }

    private void AnimateQueueRailContent(float target)
    {
        if (closed || !queueSidebarVisible || queueSidebarAnimating) return;
        QueueToggleContent.CenterPoint = new Vector3(17, 36, 0);
        var compositor = ElementCompositionPreview.GetElementVisual(Root).Compositor;
        var pulse = compositor.CreateVector3KeyFrameAnimation();
        pulse.Target = "Scale"; pulse.InsertExpressionKeyFrame(0, "this.StartingValue");
        pulse.InsertKeyFrame(1, new Vector3(target)); pulse.Duration = TimeSpan.FromMilliseconds(100);
        QueueToggleContent.StartAnimation(pulse);
    }
}
