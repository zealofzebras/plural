using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Plural.App.Controls;

/// <summary>
/// Icon drawn from a geometry on a 24x24 grid (Material style), scaled to <see cref="Size"/> and centred in a fixed square box,
/// so every icon has the same optical size and position. Colour is the inherited foreground unless <see cref="Fill"/> is set.
/// </summary>
public class Icon : Control
{
    public static readonly StyledProperty<Geometry?> DataProperty = AvaloniaProperty.Register<Icon, Geometry?>(nameof(Data));
    public static readonly StyledProperty<double> SizeProperty = AvaloniaProperty.Register<Icon, double>(nameof(Size), 20);
    public static readonly StyledProperty<IBrush?> FillProperty = AvaloniaProperty.Register<Icon, IBrush?>(nameof(Fill));

    static Icon()
    {
        AffectsRender<Icon>(DataProperty, FillProperty, TextElement.ForegroundProperty);
        AffectsMeasure<Icon>(SizeProperty);
    }

    public Geometry? Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public double Size { get => GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    public override void Render(DrawingContext context)
    {
        if (Data is not { } geometry)
        {
            return;
        }
        var brush = Fill ?? TextElement.GetForeground(this);
        var box = Bounds.Size;
        var scale = Size / 24.0;
        var dx = (box.Width - Size) / 2;
        var dy = (box.Height - Size) / 2;
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(dx, dy)))
        {
            context.DrawGeometry(brush, null, geometry);
        }
    }
}
