using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Plural.App.Controls;

/// <summary>Overview strip above the rows when zoomed in: shows the visible window over the whole take; tap or drag moves it.</summary>
public class MinimapViewport : Control
{
    public static readonly StyledProperty<double> DurationProperty = AvaloniaProperty.Register<MinimapViewport, double>(nameof(Duration));
    public static readonly StyledProperty<double> ViewStartProperty = AvaloniaProperty.Register<MinimapViewport, double>(nameof(ViewStart));
    public static readonly StyledProperty<double> ViewSpanProperty = AvaloniaProperty.Register<MinimapViewport, double>(nameof(ViewSpan));
    public static readonly StyledProperty<double> PositionProperty = AvaloniaProperty.Register<MinimapViewport, double>(nameof(Position));
    public static readonly StyledProperty<IBrush?> WindowBrushProperty = AvaloniaProperty.Register<MinimapViewport, IBrush?>(nameof(WindowBrush));
    public static readonly StyledProperty<IBrush?> PlayheadBrushProperty = AvaloniaProperty.Register<MinimapViewport, IBrush?>(nameof(PlayheadBrush));

    private bool _dragging;

    static MinimapViewport()
    {
        AffectsRender<MinimapViewport>(DurationProperty, ViewStartProperty, ViewSpanProperty, PositionProperty, WindowBrushProperty, PlayheadBrushProperty);
    }

    public double Duration { get => GetValue(DurationProperty); set => SetValue(DurationProperty, value); }
    public double ViewStart { get => GetValue(ViewStartProperty); set => SetValue(ViewStartProperty, value); }
    public double ViewSpan { get => GetValue(ViewSpanProperty); set => SetValue(ViewSpanProperty, value); }
    public double Position { get => GetValue(PositionProperty); set => SetValue(PositionProperty, value); }
    public IBrush? WindowBrush { get => GetValue(WindowBrushProperty); set => SetValue(WindowBrushProperty, value); }
    public IBrush? PlayheadBrush { get => GetValue(PlayheadBrushProperty); set => SetValue(PlayheadBrushProperty, value); }

    /// <summary>The user wants the window to start here (seconds).</summary>
    public event Action<double>? StartRequested;

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        context.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        if (Duration <= 0 || w <= 0) { return; }
        var x0 = ViewStart / Duration * w;
        var x1 = Math.Min(w, (ViewStart + ViewSpan) / Duration * w);
        var rect = new Rect(x0, 0, Math.Max(6, x1 - x0), h);
        context.DrawRectangle(WindowBrush, new Pen(PlayheadBrush, 1.5), rect.Deflate(0.75), 4, 4);
        var px = Math.Clamp(Position / Duration * w, 0, w);
        context.DrawLine(new Pen(PlayheadBrush, 1.5), new Point(px, 0), new Point(px, h));
    }

    private void Move(Point p)
    {
        if (Duration <= 0) { return; }
        StartRequested?.Invoke(p.X / Math.Max(1, Bounds.Width) * Duration - ViewSpan / 2);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _dragging = true;
        e.Pointer.Capture(this);
        Move(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging) { Move(e.GetPosition(this)); }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        e.Pointer.Capture(null);
    }
}
