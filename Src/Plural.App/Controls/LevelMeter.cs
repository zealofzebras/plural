using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Plural.App.Controls;

/// <summary>Horizontal level bar (0..100) with a green-yellow-red gradient and a peak-hold tick.</summary>
public class LevelMeter : Control
{
    public static readonly StyledProperty<double> LevelProperty = AvaloniaProperty.Register<LevelMeter, double>(nameof(Level));
    public static readonly StyledProperty<double> PeakProperty = AvaloniaProperty.Register<LevelMeter, double>(nameof(Peak));
    public static readonly StyledProperty<IBrush?> TrackBrushProperty = AvaloniaProperty.Register<LevelMeter, IBrush?>(nameof(TrackBrush));

    private static readonly LinearGradientBrush Gradient = new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.Parse("#2FCF7A"), 0),
            new GradientStop(Color.Parse("#2FCF7A"), 0.62),
            new GradientStop(Color.Parse("#F5C242"), 0.82),
            new GradientStop(Color.Parse("#FF5A5F"), 1),
        },
    };

    private static readonly IBrush PeakBrush = new SolidColorBrush(Color.Parse("#E6FFFFFF"));

    static LevelMeter()
    {
        AffectsRender<LevelMeter>(LevelProperty, PeakProperty, TrackBrushProperty);
    }

    public double Level { get => GetValue(LevelProperty); set => SetValue(LevelProperty, value); }
    public double Peak { get => GetValue(PeakProperty); set => SetValue(PeakProperty, value); }
    public IBrush? TrackBrush { get => GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0)
        {
            return;
        }
        var r = h / 2;
        context.DrawRectangle(TrackBrush, null, new Rect(0, 0, w, h), r, r);
        var fill = Math.Clamp(Level, 0, 100) / 100 * w;
        if (fill > 0.5)
        {
            using (context.PushClip(new Rect(0, 0, fill, h)))
            {
                context.DrawRectangle(Gradient, null, new Rect(0, 0, w, h), r, r);
            }
        }
        if (Peak > 1)
        {
            var x = Math.Clamp(Peak / 100 * w - 1.5, 0, w - 3);
            context.DrawRectangle(PeakBrush, null, new Rect(x, 0, 3, h), 1.5, 1.5);
        }
    }
}
