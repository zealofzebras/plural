using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;

namespace Plural.App.Controls;

/// <summary>
/// Vertical channel fader in dB with a console-style taper (0 dB at three quarters of the travel, the bottom is −∞) and a printed
/// scale. Dragging is relative, so touching the strip never makes the level jump while recording; the wheel and arrow keys move
/// it by 0.5 dB, double-tap returns to 0 dB.
/// </summary>
public class Fader : RangeBase
{
    public static readonly StyledProperty<IBrush?> GrooveBrushProperty = AvaloniaProperty.Register<Fader, IBrush?>(nameof(GrooveBrush));
    public static readonly StyledProperty<IBrush?> CapBrushProperty = AvaloniaProperty.Register<Fader, IBrush?>(nameof(CapBrush));
    public static readonly StyledProperty<IBrush?> CapLineBrushProperty = AvaloniaProperty.Register<Fader, IBrush?>(nameof(CapLineBrush));
    public static readonly StyledProperty<IBrush?> ScaleBrushProperty = AvaloniaProperty.Register<Fader, IBrush?>(nameof(ScaleBrush));

    // (dB, position 0…1): the taper and the printed scale.
    private static readonly (double Db, double Pos)[] Taper =
        [(-60, 0), (-40, 0.14), (-30, 0.24), (-20, 0.36), (-10, 0.52), (-5, 0.63), (0, 0.75), (6, 0.88), (12, 1)];

    private const double CapHeight = 40;
    private const double CapWidth = 30;
    private const double Inset = CapHeight / 2 + 2;
    private static readonly Typeface ScaleFont = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);

    private Point? _dragFrom;
    private double _dragStartPos;

    static Fader()
    {
        AffectsRender<Fader>(ValueProperty, GrooveBrushProperty, CapBrushProperty, CapLineBrushProperty, ScaleBrushProperty, IsEnabledProperty);
        FocusableProperty.OverrideDefaultValue<Fader>(true);
        MinimumProperty.OverrideDefaultValue<Fader>(-60);
        MaximumProperty.OverrideDefaultValue<Fader>(12);
    }

    public IBrush? GrooveBrush { get => GetValue(GrooveBrushProperty); set => SetValue(GrooveBrushProperty, value); }
    public IBrush? CapBrush { get => GetValue(CapBrushProperty); set => SetValue(CapBrushProperty, value); }
    public IBrush? CapLineBrush { get => GetValue(CapLineBrushProperty); set => SetValue(CapLineBrushProperty, value); }
    public IBrush? ScaleBrush { get => GetValue(ScaleBrushProperty); set => SetValue(ScaleBrushProperty, value); }

    public static double PositionOf(double db)
    {
        if (db <= Taper[0].Db)
        {
            return 0;
        }
        for (var i = 1; i < Taper.Length; i++)
        {
            if (db <= Taper[i].Db)
            {
                var (d0, p0) = Taper[i - 1];
                var (d1, p1) = Taper[i];
                return p0 + (db - d0) / (d1 - d0) * (p1 - p0);
            }
        }
        return 1;
    }

    public static double DbOf(double pos)
    {
        pos = Math.Clamp(pos, 0, 1);
        for (var i = 1; i < Taper.Length; i++)
        {
            if (pos <= Taper[i].Pos)
            {
                var (d0, p0) = Taper[i - 1];
                var (d1, p1) = Taper[i];
                return d0 + (pos - p0) / (p1 - p0) * (d1 - d0);
            }
        }
        return Taper[^1].Db;
    }

    private double Travel => Math.Max(1, Bounds.Height - 2 * Inset);
    private double YOf(double pos) => Inset + (1 - pos) * Travel;

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        if (w <= 0 || Bounds.Height <= 2 * Inset)
        {
            return;
        }
        var grooveX = w - CapWidth / 2 - 2;
        using var _ = context.PushOpacity(IsEffectivelyEnabled ? 1 : 0.5);

        // Scale: ticks and labels to the left of the groove.
        foreach (var (db, pos) in Taper)
        {
            var y = YOf(pos);
            var major = db is 0 or -10 or -30 or 12 or -60;
            var tickLeft = grooveX - CapWidth / 2 - (major ? 8 : 5);
            context.DrawLine(new Pen(ScaleBrush, db == 0 ? 1.5 : 1), new Point(tickLeft, y), new Point(grooveX - CapWidth / 2 - 1, y));
            var label = db <= Minimum ? "∞" : db > 0 ? $"+{db}" : db.ToString(CultureInfo.InvariantCulture);
            var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, ScaleFont, 9, ScaleBrush);
            context.DrawText(text, new Point(tickLeft - text.Width - 2, y - text.Height / 2));
        }

        // Groove.
        context.DrawRectangle(GrooveBrush, null, new Rect(grooveX - 2, Inset - 4, 4, Travel + 8), 2, 2);

        // Cap: rounded block with a center line, like a console fader knob.
        var capY = YOf(PositionOf(Value));
        var cap = new Rect(grooveX - CapWidth / 2, capY - CapHeight / 2, CapWidth, CapHeight);
        context.DrawRectangle(new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)), null, cap.Translate(new Vector(0, 2)), 5, 5);
        context.DrawRectangle(CapBrush, null, cap, 5, 5);
        var linePen = new Pen(CapLineBrush, 1);
        for (var i = -2; i <= 2; i++)
        {
            if (i != 0)
            {
                var ly = capY + i * 6;
                context.DrawLine(linePen, new Point(cap.Left + 6, ly), new Point(cap.Right - 6, ly));
            }
        }
        context.DrawLine(new Pen(CapLineBrush, 2.5), new Point(cap.Left + 3, capY), new Point(cap.Right - 3, capY));
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
            Value = 0;
            e.Handled = true;
            return;
        }
        _dragFrom = e.GetPosition(this);
        _dragStartPos = PositionOf(Value);
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
        var pos = _dragStartPos + (from.Y - e.GetPosition(this).Y) / Travel;
        Value = Math.Round(Math.Clamp(DbOf(pos), Minimum, Maximum), 1);
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
            Step(Math.Sign(e.Delta.Y) * 0.5);
            e.Handled = true;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var step = e.Key switch
        {
            Key.Up => 0.5,
            Key.Down => -0.5,
            Key.PageUp => 6,
            Key.PageDown => -6,
            _ => 0,
        };
        if (step != 0)
        {
            Step(step);
            e.Handled = true;
        }
    }

    private void Step(double db) =>
        Value = Math.Clamp(Value <= Minimum && db > 0 ? -40 : Value + db, Minimum, Maximum);
}
