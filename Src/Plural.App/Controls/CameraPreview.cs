using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Plural.App.Controls;

/// <summary>Draws the latest camera frame upright (rotated and mirrored as the camera asks), letterboxed.</summary>
public class CameraPreview : Control
{
    public static readonly StyledProperty<PreviewSurface?> SurfaceProperty =
        AvaloniaProperty.Register<CameraPreview, PreviewSurface?>(nameof(Surface));

    public static readonly StyledProperty<bool> CoverProperty =
        AvaloniaProperty.Register<CameraPreview, bool>(nameof(Cover));

    static CameraPreview()
    {
        AffectsRender<CameraPreview>(SurfaceProperty, CoverProperty);
        SurfaceProperty.Changed.AddClassHandler<CameraPreview>((c, e) => c.OnSurfaceChanged(e.OldValue as PreviewSurface, e.NewValue as PreviewSurface));
    }

    /// <summary>Fill the whole area (crop) instead of letterboxing.</summary>
    public bool Cover
    {
        get => GetValue(CoverProperty);
        set => SetValue(CoverProperty, value);
    }

    public PreviewSurface? Surface
    {
        get => GetValue(SurfaceProperty);
        set => SetValue(SurfaceProperty, value);
    }

    private void OnSurfaceChanged(PreviewSurface? old, PreviewSurface? value)
    {
        if (old is not null)
        {
            old.Updated -= InvalidateVisual;
        }
        if (value is not null)
        {
            value.Updated += InvalidateVisual;
        }
    }

    public override void Render(DrawingContext context)
    {
        if (Surface is not { Bitmap: { } bmp } surface)
        {
            return;
        }
        double w = bmp.PixelSize.Width, h = bmp.PixelSize.Height;
        var swap = surface.Rotation % 180 != 0;
        var dw = swap ? h : w;
        var dh = swap ? w : h;
        var b = Bounds.Size;
        var s = Cover ? Math.Max(b.Width / dw, b.Height / dh) : Math.Min(b.Width / dw, b.Height / dh);
        var m = Matrix.CreateTranslation(-w / 2, -h / 2)
                * Matrix.CreateRotation(surface.Rotation * Math.PI / 180)
                * Matrix.CreateScale(surface.Mirror ? -s : s, s)
                * Matrix.CreateTranslation(b.Width / 2, b.Height / 2);
        using (context.PushClip(new Rect(b)))
        using (context.PushTransform(m))
        {
            context.DrawImage(bmp, new Rect(0, 0, w, h));
        }
    }
}
