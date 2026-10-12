using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Plural.Core.Library;

namespace Plural.App.Controls;

/// <summary>A stretch of a track in samples (a dropout), drawn as a hatched gap.</summary>
public readonly record struct WaveRange(long StartSample, long EndSample);

/// <summary>
/// Waveform of one track from a peaks file, drawn as one filled geometry. Min/max pairs are read from the <see cref="PeakReader"/>
/// at this control's own width on a background thread (never on the UI thread); the geometry is built when they arrive.
/// <para>
/// View: <see cref="StartSample"/> is the sample at the left edge and <see cref="SamplesPerPixel"/> the zoom, per device-independent
/// pixel; 0 (the default) fits the whole track into the width. Set <see cref="Peaks"/>, or <see cref="PeakPath"/> to open a file in
/// the background. <see cref="Dropouts"/> are hatched, <see cref="Pauses"/> are thin vertical markers (both in samples of the peaks).
/// </para>
/// </summary>
public class WaveformView : Control
{
    public static readonly StyledProperty<PeakReader?> PeaksProperty = AvaloniaProperty.Register<WaveformView, PeakReader?>(nameof(Peaks));
    public static readonly StyledProperty<string?> PeakPathProperty = AvaloniaProperty.Register<WaveformView, string?>(nameof(PeakPath));
    public static readonly StyledProperty<long> StartSampleProperty = AvaloniaProperty.Register<WaveformView, long>(nameof(StartSample));
    public static readonly StyledProperty<double> SamplesPerPixelProperty = AvaloniaProperty.Register<WaveformView, double>(nameof(SamplesPerPixel));
    public static readonly StyledProperty<double> ScaleProperty = AvaloniaProperty.Register<WaveformView, double>(nameof(Scale), 1);
    public static readonly StyledProperty<IBrush?> FillProperty = AvaloniaProperty.Register<WaveformView, IBrush?>(nameof(Fill));
    public static readonly StyledProperty<IReadOnlyList<WaveRange>?> DropoutsProperty =
        AvaloniaProperty.Register<WaveformView, IReadOnlyList<WaveRange>?>(nameof(Dropouts));
    public static readonly StyledProperty<IReadOnlyList<long>?> PausesProperty =
        AvaloniaProperty.Register<WaveformView, IReadOnlyList<long>?>(nameof(Pauses));
    public static readonly StyledProperty<IBrush?> DropoutBrushProperty =
        AvaloniaProperty.Register<WaveformView, IBrush?>(nameof(DropoutBrush), new SolidColorBrush(Color.Parse("#FF5A5F")));
    public static readonly StyledProperty<IBrush?> PauseBrushProperty =
        AvaloniaProperty.Register<WaveformView, IBrush?>(nameof(PauseBrush), new SolidColorBrush(Color.Parse("#F5B53D")));

    private Geometry? _geometry;
    private bool _pending, _running, _posted;
    private string? _loadingPath;

    static WaveformView()
    {
        AffectsRender<WaveformView>(FillProperty, DropoutsProperty, PausesProperty, DropoutBrushProperty, PauseBrushProperty,
            StartSampleProperty, SamplesPerPixelProperty);
    }

    /// <summary>The peaks to draw. Shared and read-only, so a player can give the same reader to several views.</summary>
    public PeakReader? Peaks { get => GetValue(PeaksProperty); set => SetValue(PeaksProperty, value); }

    /// <summary>Convenience: opens this peaks file in the background and sets <see cref="Peaks"/>.</summary>
    public string? PeakPath { get => GetValue(PeakPathProperty); set => SetValue(PeakPathProperty, value); }

    /// <summary>Sample at the left edge.</summary>
    public long StartSample { get => GetValue(StartSampleProperty); set => SetValue(StartSampleProperty, value); }

    /// <summary>Samples per device-independent pixel; 0 = fit the whole track.</summary>
    public double SamplesPerPixel { get => GetValue(SamplesPerPixelProperty); set => SetValue(SamplesPerPixelProperty, value); }

    /// <summary>Vertical gain (1 = full scale fills the height).</summary>
    public double Scale { get => GetValue(ScaleProperty); set => SetValue(ScaleProperty, value); }

    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public IReadOnlyList<WaveRange>? Dropouts { get => GetValue(DropoutsProperty); set => SetValue(DropoutsProperty, value); }
    public IReadOnlyList<long>? Pauses { get => GetValue(PausesProperty); set => SetValue(PausesProperty, value); }
    public IBrush? DropoutBrush { get => GetValue(DropoutBrushProperty); set => SetValue(DropoutBrushProperty, value); }
    public IBrush? PauseBrush { get => GetValue(PauseBrushProperty); set => SetValue(PauseBrushProperty, value); }

    /// <summary>Effective samples per DIP for the current width (resolves 0 = fit).</summary>
    public double EffectiveSamplesPerPixel => EffectiveSpp(Peaks, Bounds.Width);

    private double EffectiveSpp(PeakReader? peaks, double width)
    {
        if (SamplesPerPixel > 0) { return SamplesPerPixel; }
        return peaks is null || width <= 0 ? 1 : Math.Max(1.0, (double)peaks.SampleCount / width);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PeakPathProperty)
        {
            _ = OpenAsync(PeakPath);
        }
        else if (change.Property == PeaksProperty || change.Property == StartSampleProperty
                 || change.Property == SamplesPerPixelProperty || change.Property == ScaleProperty)
        {
            Schedule();
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        Schedule();
    }

    private async Task OpenAsync(string? path)
    {
        _loadingPath = path;
        if (string.IsNullOrEmpty(path))
        {
            Peaks = null;
            return;
        }
        var reader = await Task.Run(() => PeakReader.Open(path));
        if (_loadingPath == path)
        {
            Peaks = reader;
        }
    }

    private void Schedule()
    {
        _pending = true;
        if (_running || _posted) { return; }
        // Start on the next dispatcher turn: the bindings of one pan / zoom step set StartSample and SamplesPerPixel one after the other,
        // and one read of the peaks is enough for both.
        _posted = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _posted = false;
            if (_running) { return; }
            _running = true;
            _ = RunAsync();
        }, Avalonia.Threading.DispatcherPriority.Render);
    }

    private async Task RunAsync()
    {
        try
        {
            while (_pending)
            {
                _pending = false;
                var peaks = Peaks;
                var width = Bounds.Width;
                var height = Bounds.Height;
                if (peaks is null || width < 1 || height < 1)
                {
                    _geometry = null;
                    InvalidateVisual();
                    continue;
                }
                var density = (VisualRoot as TopLevel)?.RenderScaling ?? 1.0;
                var columns = Math.Clamp((int)Math.Ceiling(width * density), 1, 8192);
                var start = StartSample;
                var perColumn = EffectiveSpp(peaks, width) * width / columns;
                var (min, max) = await Task.Run(() =>
                {
                    var lo = new short[columns];
                    var hi = new short[columns];
                    peaks.GetRange(start, perColumn, lo, hi);
                    return (lo, hi);
                });
                if (_pending) { continue; } // inputs changed while reading: read again
                _geometry = BuildGeometry(min, max, width, height, Scale);
                InvalidateVisual();
            }
        }
        finally
        {
            _running = false;
        }
    }

    private static Geometry BuildGeometry(short[] min, short[] max, double width, double height, double gain)
    {
        var n = min.Length;
        var step = width / n;
        var cy = height / 2;
        var amp = height / 2 * Math.Max(0.01, gain) / 32768.0;
        var hair = Math.Max(0.5, step / 2); // at least one device pixel of body, also for silence
        var g = new StreamGeometry();
        using var ctx = g.Open();
        ctx.BeginFigure(new Point(0, cy - max[0] * amp - hair), true);
        for (var i = 0; i < n; i++)
        {
            var x = i * step;
            var top = Math.Max(0, cy - max[i] * amp - hair);
            ctx.LineTo(new Point(x, top));
            ctx.LineTo(new Point(x + step, top));
        }
        for (var i = n - 1; i >= 0; i--)
        {
            var x = i * step;
            var bottom = Math.Min(height, cy - min[i] * amp + hair);
            ctx.LineTo(new Point(x + step, bottom));
            ctx.LineTo(new Point(x, bottom));
        }
        ctx.EndFigure(true);
        return g;
    }

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0) { return; }
        var peaks = Peaks;
        var spp = EffectiveSpp(peaks, w);

        if (Dropouts is { Count: > 0 } drops)
        {
            var pen = new Pen(DropoutBrush, 1);
            foreach (var d in drops)
            {
                var x0 = Math.Max(0, (d.StartSample - StartSample) / spp);
                var x1 = Math.Min(w, (d.EndSample - StartSample) / spp);
                if (x1 <= x0 || x1 < 0 || x0 > w) { continue; }
                var rect = new Rect(x0, 0, Math.Max(2, x1 - x0), h);
                using (context.PushClip(rect))
                using (context.PushOpacity(0.18))
                {
                    context.DrawRectangle(DropoutBrush, null, rect);
                }
                using (context.PushClip(rect))
                using (context.PushOpacity(0.7))
                {
                    for (var x = rect.X - h; x < rect.Right; x += 7)
                    {
                        context.DrawLine(pen, new Point(x, h), new Point(x + h, 0));
                    }
                }
            }
        }

        if (_geometry is { } geometry && Fill is { } fill)
        {
            context.DrawGeometry(fill, null, geometry);
        }

        if (Pauses is { Count: > 0 } pauses)
        {
            var pen = new Pen(PauseBrush, 1.5);
            foreach (var p in pauses)
            {
                var x = (p - StartSample) / spp;
                if (x >= 0 && x <= w)
                {
                    context.DrawLine(pen, new Point(x, 0), new Point(x, h));
                }
            }
        }
    }
}
