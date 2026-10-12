using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Plural.App.Controls;

/// <summary>
/// Vertical LED-segment meter for the mixer. <see cref="Level"/> and <see cref="Peak"/> use the same 0…100 scale as
/// <see cref="LevelMeter"/> (−60 … 0 dBFS); green below −12 dB, amber to −3 dB, red above.
/// </summary>
public class MixerMeter : Control
{
    public static readonly StyledProperty<double> LevelProperty = AvaloniaProperty.Register<MixerMeter, double>(nameof(Level));
    public static readonly StyledProperty<double> PeakProperty = AvaloniaProperty.Register<MixerMeter, double>(nameof(Peak));
    public static readonly StyledProperty<IBrush?> TrackBrushProperty = AvaloniaProperty.Register<MixerMeter, IBrush?>(nameof(TrackBrush));

    private static readonly IBrush Green = new SolidColorBrush(Color.Parse("#2FCF7A"));
    private static readonly IBrush Amber = new SolidColorBrush(Color.Parse("#F5C242"));
    private static readonly IBrush Red = new SolidColorBrush(Color.Parse("#FF5A5F"));
    private const double Segment = 3;
    private const double Gap = 1.5;

    static MixerMeter()
    {
        AffectsRender<MixerMeter>(LevelProperty, PeakProperty, TrackBrushProperty);
    }

    public double Level { get => GetValue(LevelProperty); set => SetValue(LevelProperty, value); }
    public double Peak { get => GetValue(PeakProperty); set => SetValue(PeakProperty, value); }
    public IBrush? TrackBrush { get => GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        var count = (int)((h + Gap) / (Segment + Gap));
        if (w <= 0 || count <= 0)
        {
            return;
        }
        var lit = (int)Math.Round(Math.Clamp(Level, 0, 100) / 100 * count);
        var peak = Peak > 1 ? Math.Clamp((int)Math.Round(Peak / 100 * count), 1, count) : 0;
        for (var i = 0; i < count; i++)
        {
            var y = h - (i + 1) * (Segment + Gap) + Gap;
            var fraction = (i + 1.0) / count;
            var on = i < lit || i == peak - 1;
            var brush = on ? fraction > 0.95 ? Red : fraction > 0.8 ? Amber : Green : TrackBrush;
            context.DrawRectangle(brush, null, new Rect(0, y, w, Segment), 1, 1);
        }
    }
}
