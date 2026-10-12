using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Plural.App.Controls;

/// <summary>
/// The shared timeline of the Player page, laid over the ruler strip and the track rows: draws the time ruler and the playhead and
/// turns pointer input into seek / zoom / scroll requests (one pointer: tap to seek, drag the playhead or the ruler to scrub, drag
/// sideways to scroll, drag up/down to scroll the rows; two touches: pinch to zoom; Ctrl+wheel zooms, Shift+wheel scrolls).
/// All times are seconds. It owns no state: the view model feeds <see cref="Duration"/>, <see cref="ViewStart"/>, <see cref="ViewSpan"/>
/// and <see cref="Position"/>, and answers the events.
/// </summary>
public class TimelineSurface : Control
{
    public static readonly StyledProperty<double> DurationProperty = AvaloniaProperty.Register<TimelineSurface, double>(nameof(Duration));
    public static readonly StyledProperty<double> ViewStartProperty = AvaloniaProperty.Register<TimelineSurface, double>(nameof(ViewStart));
    public static readonly StyledProperty<double> ViewSpanProperty = AvaloniaProperty.Register<TimelineSurface, double>(nameof(ViewSpan));
    public static readonly StyledProperty<double> PositionProperty = AvaloniaProperty.Register<TimelineSurface, double>(nameof(Position));
    public static readonly StyledProperty<double> RulerHeightProperty = AvaloniaProperty.Register<TimelineSurface, double>(nameof(RulerHeight), 28);
    public static readonly StyledProperty<IBrush?> RulerBrushProperty = AvaloniaProperty.Register<TimelineSurface, IBrush?>(nameof(RulerBrush));
    public static readonly StyledProperty<IBrush?> TickBrushProperty = AvaloniaProperty.Register<TimelineSurface, IBrush?>(nameof(TickBrush));
    public static readonly StyledProperty<IBrush?> PlayheadBrushProperty = AvaloniaProperty.Register<TimelineSurface, IBrush?>(nameof(PlayheadBrush));

    private static readonly double[] Steps = [0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200];
    private static readonly Typeface TickFont = new(FontFamily.Default, FontStyle.Normal, FontWeight.Medium);

    private static readonly StreamGeometry PlayheadMarker = CreateMarker();

    private enum Mode { None, Pending, Pan, Scrub, Vertical, Pinch }

    private readonly Dictionary<string, FormattedText> _labels = [];
    private IBrush? _penBrush, _playBrush;
    private Pen? _tickPen, _playPen;
    private bool _scrubOpen; // a non-final seek was sent and the final one is still owed

    private readonly Dictionary<int, Point> _pointers = [];
    private Mode _mode;
    private Point _down;
    private Point _last;
    private double _pinchStartDistance, _pinchStartSpan, _pinchAnchorTime;

    static TimelineSurface()
    {
        AffectsRender<TimelineSurface>(DurationProperty, ViewStartProperty, ViewSpanProperty, PositionProperty, RulerHeightProperty,
            RulerBrushProperty, TickBrushProperty, PlayheadBrushProperty);
    }

    public double Duration { get => GetValue(DurationProperty); set => SetValue(DurationProperty, value); }
    public double ViewStart { get => GetValue(ViewStartProperty); set => SetValue(ViewStartProperty, value); }
    public double ViewSpan { get => GetValue(ViewSpanProperty); set => SetValue(ViewSpanProperty, value); }
    public double Position { get => GetValue(PositionProperty); set => SetValue(PositionProperty, value); }
    public double RulerHeight { get => GetValue(RulerHeightProperty); set => SetValue(RulerHeightProperty, value); }
    public IBrush? RulerBrush { get => GetValue(RulerBrushProperty); set => SetValue(RulerBrushProperty, value); }
    public IBrush? TickBrush { get => GetValue(TickBrushProperty); set => SetValue(TickBrushProperty, value); }
    public IBrush? PlayheadBrush { get => GetValue(PlayheadBrushProperty); set => SetValue(PlayheadBrushProperty, value); }

    /// <summary>(seconds, final): seek here; <c>final</c> is false while scrubbing.</summary>
    public event Action<double, bool>? SeekRequested;

    /// <summary>(start, span): the user wants this view.</summary>
    public event Action<double, double>? ViewRequested;

    /// <summary>Scroll the track rows by this many dips (positive = down).</summary>
    public event Action<double>? VerticalScrollRequested;

    private double Span => ViewSpan > 0 ? ViewSpan : Math.Max(1, Duration);
    private double X(double t) => (t - ViewStart) / Span * Bounds.Width;
    private double TimeAt(double x) => ViewStart + Math.Clamp(x / Math.Max(1, Bounds.Width), 0, 1) * Span;
    private bool ZoomedOut => ViewSpan >= Duration * 0.999;

    // ------------------------------------------------------------------ drawing

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        context.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h)); // hit-test everywhere
        if (w <= 0 || h <= 0 || Duration <= 0) { return; }

        var rh = RulerHeight;
        context.DrawRectangle(RulerBrush, null, new Rect(0, 0, w, rh));
        var tick = TickBrush;
        if (_tickPen is null || !ReferenceEquals(tick, _penBrush))
        {
            _penBrush = tick;
            _tickPen = new Pen(tick, 1);
            _labels.Clear();
        }
        var pen = _tickPen!;
        var span = Span;
        var pxPerSec = w / span;
        var step = Steps[^1];
        foreach (var s in Steps)
        {
            if (s * pxPerSec >= 72) { step = s; break; }
        }
        var first = Math.Floor(ViewStart / step) * step;
        for (var t = first; t <= ViewStart + span + step; t += step)
        {
            var x = X(t);
            if (x < -40 || x > w + 40) { continue; }
            context.DrawLine(pen, new Point(x, rh - 9), new Point(x, rh));
            context.DrawText(Label(Format(t, step)), new Point(x + 4, 3));
            // half-step minor tick
            var xm = X(t + step / 2);
            if (xm >= 0 && xm <= w) { context.DrawLine(pen, new Point(xm, rh - 5), new Point(xm, rh)); }
        }
        context.DrawLine(pen, new Point(0, rh - 0.5), new Point(w, rh - 0.5));

        // playhead
        var px = X(Position);
        if (px >= -2 && px <= w + 2)
        {
            var brush = PlayheadBrush ?? Brushes.White;
            if (_playPen is null || !ReferenceEquals(brush, _playBrush))
            {
                _playBrush = brush;
                _playPen = new Pen(brush, 2);
            }
            context.DrawLine(_playPen!, new Point(px, 0), new Point(px, h));
            using (context.PushTransform(Matrix.CreateTranslation(px, 0)))
            {
                context.DrawGeometry(brush, null, PlayheadMarker);
            }
        }
    }

    /// <summary>Ruler label, cached by text (a pan only changes the position, a zoom a handful of strings).</summary>
    private FormattedText Label(string text)
    {
        if (!_labels.TryGetValue(text, out var label))
        {
            if (_labels.Count > 256) { _labels.Clear(); }
            label = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, TickFont, 11, _penBrush);
            _labels[text] = label;
        }
        return label;
    }

    private static StreamGeometry CreateMarker()
    {
        var tri = new StreamGeometry();
        using (var g = tri.Open())
        {
            g.BeginFigure(new Point(-7, 0), true);
            g.LineTo(new Point(7, 0));
            g.LineTo(new Point(0, 11));
            g.EndFigure(true);
        }
        return tri;
    }

    private static string Format(double t, double step)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, t));
        if (step < 1) { return $"{(int)ts.TotalMinutes}:{ts.Seconds:00}.{ts.Milliseconds / 100}"; }
        return ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}" : $"{ts.Minutes}:{ts.Seconds:00}";
    }

    // ------------------------------------------------------------------ pointer input

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var pt = e.GetCurrentPoint(this);
        if (e.Pointer.Type == PointerType.Mouse && !pt.Properties.IsLeftButtonPressed) { return; }
        var pos = pt.Position;
        _pointers[e.Pointer.Id] = pos;
        e.Pointer.Capture(this);
        e.Handled = true;
        if (_pointers.Count >= 2)
        {
            StartPinch();
            return;
        }
        _down = _last = pos;
        var nearPlayhead = Math.Abs(X(Position) - pos.X) <= 22;
        if (pos.Y < RulerHeight || nearPlayhead)
        {
            _mode = Mode.Scrub;
            RaiseSeek(TimeAt(pos.X), false);
        }
        else
        {
            _mode = Mode.Pending;
        }
    }

    private void StartPinch()
    {
        _mode = Mode.Pinch;
        var (a, b) = TwoPoints();
        _pinchStartDistance = Math.Max(24, Math.Abs(a.X - b.X));
        _pinchStartSpan = Span;
        _pinchAnchorTime = TimeAt((a.X + b.X) / 2);
    }

    private (Point, Point) TwoPoints()
    {
        Point a = default, b = default;
        var i = 0;
        foreach (var p in _pointers.Values)
        {
            if (i == 0) { a = p; } else if (i == 1) { b = p; }
            i++;
        }
        return (a, b);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_pointers.ContainsKey(e.Pointer.Id)) { return; }
        var pos = e.GetPosition(this);
        _pointers[e.Pointer.Id] = pos;
        if (_mode == Mode.Pinch)
        {
            if (_pointers.Count >= 2)
            {
                var (a, b) = TwoPoints();
                var dist = Math.Max(24, Math.Abs(a.X - b.X));
                var span = _pinchStartSpan * _pinchStartDistance / dist;
                var centerFrac = Math.Clamp((a.X + b.X) / 2 / Math.Max(1, Bounds.Width), 0, 1);
                ViewRequested?.Invoke(_pinchAnchorTime - centerFrac * span, span);
            }
        }
        else
        {
            if (_mode == Mode.Pending)
            {
                var d = pos - _down;
                if (Math.Abs(d.X) >= 8 || Math.Abs(d.Y) >= 8)
                {
                    _mode = Math.Abs(d.X) >= Math.Abs(d.Y) ? (ZoomedOut ? Mode.Scrub : Mode.Pan) : Mode.Vertical;
                    _last = _down;
                }
            }
            switch (_mode)
            {
                case Mode.Pan:
                    ViewRequested?.Invoke(ViewStart - (pos.X - _last.X) / Math.Max(1, Bounds.Width) * Span, Span);
                    _last = pos;
                    break;
                case Mode.Vertical:
                    VerticalScrollRequested?.Invoke(-(pos.Y - _last.Y));
                    _last = pos;
                    break;
                case Mode.Scrub:
                    RaiseSeek(TimeAt(pos.X), false);
                    break;
            }
        }
        e.Handled = true;
    }

    private void RaiseSeek(double seconds, bool final)
    {
        _scrubOpen = !final;
        SeekRequested?.Invoke(seconds, final);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_pointers.Remove(e.Pointer.Id)) { return; }
        e.Pointer.Capture(null);
        var pos = e.GetPosition(this);
        switch (_mode)
        {
            case Mode.Pending:
                RaiseSeek(TimeAt(pos.X), true); // a tap
                break;
            case Mode.Scrub:
                RaiseSeek(TimeAt(pos.X), true);
                break;
            case Mode.Pinch:
                // a pinch that began as a scrub (second finger down) still owes the final seek, or the player stays in scrub state
                if (_scrubOpen) { RaiseSeek(Position, true); }
                break;
        }
        _mode = Mode.None;
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_pointers.Remove(e.Pointer.Id) && _pointers.Count == 0)
        {
            if (_mode == Mode.Scrub || _scrubOpen) { RaiseSeek(Position, true); }
            _mode = Mode.None;
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var x = e.GetPosition(this).X;
        if ((e.KeyModifiers & KeyModifiers.Control) != 0)
        {
            var factor = Math.Pow(0.8, e.Delta.Y);
            var anchor = TimeAt(x);
            var frac = Math.Clamp(x / Math.Max(1, Bounds.Width), 0, 1);
            var span = Span * factor;
            ViewRequested?.Invoke(anchor - frac * span, span);
        }
        else if ((e.KeyModifiers & KeyModifiers.Shift) != 0 || Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y))
        {
            var d = Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y) ? e.Delta.X : e.Delta.Y;
            ViewRequested?.Invoke(ViewStart - d * 0.15 * Span, Span);
        }
        else
        {
            VerticalScrollRequested?.Invoke(-e.Delta.Y * 56);
        }
        e.Handled = true;
    }
}
