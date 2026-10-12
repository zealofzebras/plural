using System;
using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;

namespace Plural.App.Controls;

/// <summary>Compact horizontal gain control for the track rows (dB): groove, 0 dB tick and a round knob. Drag, wheel and arrow keys; double-tap resets to 0 dB.</summary>
public class GainBar : RangeBase
{
    public static readonly StyledProperty<IBrush?> GrooveBrushProperty = AvaloniaProperty.Register<GainBar, IBrush?>(nameof(GrooveBrush));
    public static readonly StyledProperty<IBrush?> FillBrushProperty = AvaloniaProperty.Register<GainBar, IBrush?>(nameof(FillBrush));
    public static readonly StyledProperty<IBrush?> KnobBrushProperty = AvaloniaProperty.Register<GainBar, IBrush?>(nameof(KnobBrush));

    private const double Knob = 16;
    private bool _dragging;

    static GainBar()
    {
        AffectsRender<GainBar>(ValueProperty, MinimumProperty, MaximumProperty, GrooveBrushProperty, FillBrushProperty, KnobBrushProperty);
        FocusableProperty.OverrideDefaultValue<GainBar>(true);
        MinimumProperty.OverrideDefaultValue<GainBar>(-40);
        MaximumProperty.OverrideDefaultValue<GainBar>(12);
    }

    public IBrush? GrooveBrush { get => GetValue(GrooveBrushProperty); set => SetValue(GrooveBrushProperty, value); }
    public IBrush? FillBrush { get => GetValue(FillBrushProperty); set => SetValue(FillBrushProperty, value); }
    public IBrush? KnobBrush { get => GetValue(KnobBrushProperty); set => SetValue(KnobBrushProperty, value); }

    private double Travel => Math.Max(1, Bounds.Width - Knob);
    private double XOf(double db) => Knob / 2 + (db - Minimum) / (Maximum - Minimum) * Travel;

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        context.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        var cy = h / 2;
        var x = XOf(Value);
        context.DrawRectangle(GrooveBrush, null, new Rect(Knob / 2, cy - 2, w - Knob, 4), 2, 2);
        var zero = XOf(0);
        context.DrawRectangle(FillBrush, null, new Rect(Math.Min(zero, x), cy - 2, Math.Abs(x - zero), 4), 2, 2);
        context.DrawLine(new Pen(GrooveBrush, 1.5), new Point(zero, cy - 7), new Point(zero, cy + 7));
        context.DrawEllipse(KnobBrush, null, new Point(x, cy), Knob / 2, Knob / 2);
    }

    private void SetFrom(Point p) =>
        Value = Math.Round(Math.Clamp(Minimum + (p.X - Knob / 2) / Travel * (Maximum - Minimum), Minimum, Maximum), 1);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.ClickCount == 2) { Value = 0; e.Handled = true; return; }
        _dragging = true;
        e.Pointer.Capture(this);
        SetFrom(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging) { SetFrom(e.GetPosition(this)); e.Handled = true; }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        Value = Math.Clamp(Value + Math.Sign(e.Delta.Y) * 0.5, Minimum, Maximum);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var d = e.Key is Key.Right or Key.Up ? 0.5 : e.Key is Key.Left or Key.Down ? -0.5 : 0;
        if (d != 0) { Value = Math.Clamp(Value + d, Minimum, Maximum); e.Handled = true; }
    }
}
