using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace Vesper.App;

public sealed partial class MainWindow
{
    private CompositionRoundedRectangleGeometry? browserContentClipGeometry;
    private CompositionGeometricClip? browserContentGeometricClip;

    private void OnBrowserContentClipLoaded(object sender, RoutedEventArgs args)
    {
        if (closed) return;
        // This wrapper owns only its clip. Layout/transition transforms stay on
        // its children, so its handout visual is never mixed with XAML facades.
        var visual = ElementCompositionPreview.GetElementVisual(BrowserContentClip);
        if (browserContentClipGeometry is null)
        {
            browserContentClipGeometry = visual.Compositor.CreateRoundedRectangleGeometry();
            browserContentClipGeometry.CornerRadius = new Vector2(7);
            browserContentGeometricClip = visual.Compositor.CreateGeometricClip(browserContentClipGeometry);
        }
        visual.Clip = browserContentGeometricClip;
        UpdateBrowserContentClipSize(BrowserContentClip.ActualWidth, BrowserContentClip.ActualHeight);
    }

    private void OnBrowserContentClipSizeChanged(object sender, SizeChangedEventArgs args) =>
        UpdateBrowserContentClipSize(args.NewSize.Width, args.NewSize.Height);

    private void UpdateBrowserContentClipSize(double width, double height)
    {
        if (closed || browserContentClipGeometry is null) return;
        var size = new Vector2((float)Math.Max(0, width), (float)Math.Max(0, height));
        if (browserContentClipGeometry.Size != size) browserContentClipGeometry.Size = size;
    }
}
