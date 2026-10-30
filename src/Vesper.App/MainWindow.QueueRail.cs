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
    private Storyboard? queueRailHintAnimation;
    private Vector3KeyFrameAnimation? queueRailHintSlide;
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
        if (closed || QueueShellHover.ActualWidth <= 0) return;
        peek = peek && !queuePanelOpen && !queueLayoutAnimating;
        double from = QueueRailHintTransform.ScaleX;
        queueRailHintAnimation?.Stop();
        QueueRailHintTransform.ScaleX = from;
        // Scale around the right edge: the rail grows four units to the left,
        // while its right edge stays exactly aligned with the window's cards.
        // The wrapper has its own traditional transform; the chrome's card
        // transition is a separate child transform.
        double target = 1 + (peek ? 4 / QueueShellHover.ActualWidth : 0);
        var storyboard = new Storyboard();
        var expand = new DoubleAnimation
        {
            From = from, To = target, Duration = TimeSpan.FromMilliseconds(140),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(expand, QueueRailHintTransform);
        Storyboard.SetTargetProperty(expand, nameof(Microsoft.UI.Xaml.Media.CompositeTransform.ScaleX));
        storyboard.Children.Add(expand);
        storyboard.Completed += (_, _) =>
        {
            if (closed || !ReferenceEquals(queueRailHintAnimation, storyboard)) return;
            storyboard.Stop(); QueueRailHintTransform.ScaleX = target; queueRailHintAnimation = null;
        };
        queueRailHintAnimation = storyboard; storyboard.Begin();

        var compositor = ElementCompositionPreview.GetElementVisual(Root).Compositor;
        var slide = compositor.CreateVector3KeyFrameAnimation();
        slide.Target = "Translation"; slide.InsertExpressionKeyFrame(0, "this.StartingValue");
        slide.InsertKeyFrame(1, new Vector3(peek ? -2 : 0, 0, 0)); slide.Duration = TimeSpan.FromMilliseconds(140);
        queueRailHintSlide = slide;
        QueueToggleContent.StartAnimation(slide);
        AnimateQueueRailContent(1);
    }

    private void ResetQueueRailHint()
    {
        queueRailHintAnimation?.Stop(); queueRailHintAnimation = null;
        QueueRailHintTransform.ScaleX = 1;
        if (closed) return;
        if (queueRailHintSlide is { } slide) QueueToggleContent.StopAnimation(slide);
        queueRailHintSlide = null;
        QueueToggleContent.Translation = Vector3.Zero;
    }

    private void AnimateQueueRailContent(float target)
    {
        QueueToggleContent.CenterPoint = new Vector3(17, 36, 0);
        var compositor = ElementCompositionPreview.GetElementVisual(Root).Compositor;
        var pulse = compositor.CreateVector3KeyFrameAnimation();
        pulse.Target = "Scale"; pulse.InsertExpressionKeyFrame(0, "this.StartingValue");
        pulse.InsertKeyFrame(1, new Vector3(target)); pulse.Duration = TimeSpan.FromMilliseconds(100);
        QueueToggleContent.StartAnimation(pulse);
    }
}
