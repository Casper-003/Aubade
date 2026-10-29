using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Vesper.App;

internal sealed class SongRowHoverGroup
{
    private SongRowSurface? current;
    internal void Enter(SongRowSurface row)
    {
        if (!ReferenceEquals(current, row))
        {
            current?.ApplyHoverState(false);
            current = row;
        }
        row.ApplyHoverState(true);
    }
    internal void Leave(SongRowSurface row)
    {
        if (ReferenceEquals(current, row)) current = null;
        row.ApplyHoverState(false);
    }
}

// Stable XAML stripe; pointer feedback changes only a cached composition layer below the text/buttons.
public sealed class SongRowSurface : Grid
{
    private Brush? normalBrush, hoverBrush;
    private readonly Grid hoverHost = new() { IsHitTestVisible = false, Margin = new Thickness(-8) };
    private ShapeVisual? hoverVisual;
    private CompositionRoundedRectangleGeometry? hoverGeometry;
    private CompositionColorBrush? hoverColor;
    internal SongRowHoverGroup? HoverGroup { get; set; }
    public int RowIndex { get; private set; } = -1;
    public bool IsHovered { get; private set; }
    internal Brush? ActiveBrush => IsHovered ? hoverBrush : normalBrush;
    internal bool HasVisibleHoverLayer => hoverVisual?.Opacity == 1;
    internal Windows.UI.Color? HoverLayerColor => hoverColor?.Color;
    public SongRowSurface()
    {
        CornerRadius = new CornerRadius(8);
        SetColumnSpan(hoverHost, 3);
        Children.Add(hoverHost);
        hoverHost.Loaded += (_, _) => EnsureHoverVisual();
        hoverHost.SizeChanged += (_, _) => ResizeHoverVisual();
        DataContextChanged += (_, _) => SetHovered(false);
        Unloaded += (_, _) => SetHovered(false);
        AddHandler(PointerEnteredEvent, new PointerEventHandler((_, _) => SetHovered(true)), true);
        AddHandler(PointerMovedEvent, new PointerEventHandler((_, _) => { if (!IsHovered) SetHovered(true); }), true);
        AddHandler(PointerExitedEvent, new PointerEventHandler((_, _) => SetHovered(false)), true);
        AddHandler(PointerCanceledEvent, new PointerEventHandler((_, _) => SetHovered(false)), true);
    }
    public void Prepare(int index, Brush normal, Brush hover)
    {
        RowIndex = index;
        normalBrush = normal; hoverBrush = hover;
        SetHovered(false);
        if (!ReferenceEquals(Background, normal)) Background = normal;
        RefreshHoverPalette();
    }
    internal void SetHovered(bool hovered)
    {
        if (HoverGroup is { } group)
        {
            if (hovered) group.Enter(this); else group.Leave(this);
        }
        else ApplyHoverState(hovered);
    }
    internal void ApplyHoverState(bool hovered)
    {
        if (IsHovered == hovered) return;
        IsHovered = hovered;
        if (hoverVisual is not null) hoverVisual.Opacity = hovered ? 1 : 0;
    }
    internal void RefreshHoverPalette()
    {
        if (hoverColor is not null && hoverBrush is SolidColorBrush brush && hoverColor.Color != brush.Color)
            hoverColor.Color = brush.Color;
    }
    private void EnsureHoverVisual()
    {
        if (hoverVisual is not null) { ResizeHoverVisual(); return; }
        var compositor = ElementCompositionPreview.GetElementVisual(hoverHost).Compositor;
        hoverGeometry = compositor.CreateRoundedRectangleGeometry();
        hoverGeometry.CornerRadius = new Vector2(8);
        hoverColor = compositor.CreateColorBrush();
        var shape = compositor.CreateSpriteShape(hoverGeometry);
        shape.FillBrush = hoverColor;
        hoverVisual = compositor.CreateShapeVisual();
        hoverVisual.Shapes.Add(shape);
        hoverVisual.Opacity = IsHovered ? 1 : 0;
        ElementCompositionPreview.SetElementChildVisual(hoverHost, hoverVisual);
        RefreshHoverPalette(); ResizeHoverVisual();
    }
    private void ResizeHoverVisual()
    {
        if (hoverVisual is null || hoverGeometry is null) return;
        var size = new Vector2((float)hoverHost.ActualWidth, (float)hoverHost.ActualHeight);
        if (hoverVisual.Size != size) { hoverVisual.Size = size; hoverGeometry.Size = size; }
    }
}