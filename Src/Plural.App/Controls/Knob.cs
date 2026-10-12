using System;
using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;

namespace Plural.App.Controls;

/// <summary>
/// Rotary knob for the mixer (270° sweep). Drag up/down or use the wheel or arrow keys to change it; double-tap resets it to
/// <see cref="DefaultValue"/>.
/// </summary>
public class Knob : RangeBase
{
    public static readonly StyledProperty<double> DefaultValueProperty = AvaloniaProperty.Register<Knob, double>(nameof(DefaultValue));
    public static readonly StyledProperty<IBrush?> TrackBrushProperty = AvaloniaProperty.Register<Knob, IBrush?>(nameof(TrackBrush));
    public static readonly StyledProperty<IBrush?> ValueBrushProperty = AvaloniaProperty.Register<Knob, IBrush?>(nameof(ValueBrush));
    public static readonly StyledProperty<IBrush?> CapBrushProperty = AvaloniaProperty.Register<Knob, IBrush?>(nameof(CapBrush));
    public static readonly StyledProperty<IBrush?> PointerBrushProperty = AvaloniaProperty.Register<Knob, IBrush?>(nameof(PointerBrush));

    private const double StartAngle = 135;
    private const double Sweep = 270;
    private const double DragPixelsForFullRange = 160;

    private Point? _dragFrom;
    private double _dragStartValue;

    static Knob()
    {
        AffectsRender<Knob>(ValueProperty, MinimumProperty, MaximumProperty, TrackBrushProperty, ValueBrushProperty, CapBrushProperty,
            PointerBrushProperty, IsEnabledProperty);
        FocusableProperty.OverrideDefaultValue<Knob>(true);
    }

    public double DefaultValue { get => GetValue(DefaultValueProperty); set => SetValue(DefaultValueProperty, value); }
    public IBrush? TrackBrush { get => GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public IBrush? ValueBrush { get => GetValue(ValueBrushProperty); set => SetValue(ValueBrushProperty, value); }
    public IBrush? CapBrush { get => GetValue(CapBrushProperty); set => SetValue(CapBrushProperty, value); }
    public IBrush? PointerBrush { get => GetValue(PointerBrushProperty); set => SetValue(PointerBrushProperty, value); }

    private double Fraction => Maximum > Minimum ? Math.Clamp((Value - Minimum) / (Maximum - Minimum), 0, 1) : 0;

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 4)
        {
            return;
        }
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var stroke = Math.Max(3, size * 0.09);
        var radius = size / 2 - stroke / 2 - 1;
        using (context.PushOpacity(IsEffectivelyEnabled ? 1 : 0.4))
        {
            DrawArc(context, center, radius, StartAngle, Sweep, new Pen(TrackBrush, stroke, lineCap: PenLineCap.Round));
            if (Fraction > 0.001)
            {
                DrawArc(context, center, radius, StartAngle, Sweep * Fraction, new Pen(ValueBrush, stroke, lineCap: PenLineCap.Round));
            }
            var capRadius = radius - stroke - 2;
            context.DrawEllipse(CapBrush, null, center, capRadius, capRadius);
            var angle = (StartAngle + Sweep * Fraction) * Math.PI / 180;
            var dir = new Vector(Math.Cos(angle), Math.Sin(angle));
            context.DrawLine(new Pen(PointerBrush, Math.Max(2, size * 0.06), lineCap: PenLineCap.Round),
                center + dir * capRadius * 0.25, center + dir * capRadius * 0.85);
        }
    }

    private static void DrawArc(DrawingContext context, Point center, double radius, double startDeg, double sweepDeg, IPen pen)
    {
        static Point At(Point c, double r, double deg) =>
            new(c.X + r * Math.Cos(deg * Math.PI / 180), c.Y + r * Math.Sin(deg * Math.PI / 180));
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(At(center, radius, startDeg), false);
            g.ArcTo(At(center, radius, startDeg + sweepDeg), new Size(radius, radius), 0, sweepDeg > 180, SweepDirection.Clockwise);
            g.EndFigure(false);
        }
        context.DrawGeometry(null, pen, geometry);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!IsEffectivelyEnabled)
        {
            return;
        }
        if (e.ClickCount == 2)
        {
            Value = DefaultValue;
            e.Handled = true;
            return;
        }
        _dragFrom = e.GetPosition(this);
        _dragStartValue = Value;
        e.Pointer.Capture(this);
        Focus();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragFrom is not { } from)
        {
            return;
        }
        var p = e.GetPosition(this);
        // Up (and right) increases, like a hardware knob dragged with a fader-style gesture.
        var delta = (from.Y - p.Y + (p.X - from.X) * 0.5) / DragPixelsForFullRange * (Maximum - Minimum);
        Value = Math.Clamp(_dragStartValue + delta, Minimum, Maximum);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragFrom is not null)
        {
            _dragFrom = null;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragFrom = null;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (IsEffectivelyEnabled)
        {
            Value = Math.Clamp(Value + Math.Sign(e.Delta.Y) * SmallChange, Minimum, Maximum);
            e.Handled = true;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var step = e.Key switch
        {
            Key.Up or Key.Right => SmallChange,
            Key.Down or Key.Left => -SmallChange,
            Key.PageUp => LargeChange,
            Key.PageDown => -LargeChange,
            _ => 0,
        };
        if (step != 0)
        {
            Value = Math.Clamp(Value + step, Minimum, Maximum);
            e.Handled = true;
        }
    }
}
