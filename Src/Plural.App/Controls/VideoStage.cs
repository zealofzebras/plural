using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Plural.App.ViewModels;
using CoreArea = Plural.Core.Playback.Area;
using CoreRect = Plural.Core.Playback.Rect;
using FormFactor = Plural.Core.Playback.FormFactor;
using VideoLayout = Plural.Core.Playback.VideoLayout;

namespace Plural.App.Controls;

/// <summary>
/// The video area of the Player page: draws every video tile (rotated frame, poster or a placeholder) at the rectangle that
/// <see cref="VideoLayout.Compute"/> gives for the current size, form factor and spotlight. One control draws all tiles, so the
/// 150 ms layout animation (desktop only) is just interpolating rectangles. Tap = spotlight, double-tap = fullscreen (events).
/// </summary>
public class VideoStage : Control
{
    public static readonly StyledProperty<IReadOnlyList<VideoTileViewModel>?> TilesProperty =
        AvaloniaProperty.Register<VideoStage, IReadOnlyList<VideoTileViewModel>?>(nameof(Tiles));
    public static readonly StyledProperty<int> SpotlightProperty = AvaloniaProperty.Register<VideoStage, int>(nameof(Spotlight), -1);
    public static readonly StyledProperty<int> FullscreenProperty = AvaloniaProperty.Register<VideoStage, int>(nameof(Fullscreen), -1);
    public static readonly StyledProperty<FormFactor> FormProperty = AvaloniaProperty.Register<VideoStage, FormFactor>(nameof(Form));
    public static readonly StyledProperty<bool> AnimateProperty = AvaloniaProperty.Register<VideoStage, bool>(nameof(Animate));
    public static readonly StyledProperty<IBrush?> AccentProperty = AvaloniaProperty.Register<VideoStage, IBrush?>(nameof(Accent));

    private const double AnimationMs = 150;
    private static readonly Typeface LabelFont = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
    private static readonly IBrush TileBack = new SolidColorBrush(Color.Parse("#14171E"));
    private static readonly IBrush LabelBack = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0));
    private static readonly IBrush PosterShade = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0));
    private static readonly IBrush IconBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255));

    private IReadOnlyList<VideoTileViewModel> _tiles = [];
    private CoreRect[] _target = [];
    private CoreRect[] _from = [];
    private CoreArea _lastArea;
    private long _animStart;
    private bool _animating;
    private CoreRect[] _scratch = [];
    private readonly Dictionary<VideoTileViewModel, (double Width, FormattedText Text)> _labelCache = [];
    private IBrush? _accentBrush;
    private Pen? _accentPen;
    private Geometry? _videoOff;

    static VideoStage()
    {
        AffectsRender<VideoStage>(AccentProperty);
        TilesProperty.Changed.AddClassHandler<VideoStage>((s, e) => s.OnTilesChanged(e.OldValue as IReadOnlyList<VideoTileViewModel>, e.NewValue as IReadOnlyList<VideoTileViewModel>));
        SpotlightProperty.Changed.AddClassHandler<VideoStage>((s, _) => s.Relayout(true));
        FullscreenProperty.Changed.AddClassHandler<VideoStage>((s, _) => s.Relayout(true));
        FormProperty.Changed.AddClassHandler<VideoStage>((s, _) => s.Relayout(false));
    }

    public VideoStage()
    {
        Tapped += (_, e) =>
        {
            if (HitTile(e.GetPosition(this)) is { } i) { TileTapped?.Invoke(i); }
        };
        DoubleTapped += (_, e) =>
        {
            if (HitTile(e.GetPosition(this)) is { } i) { TileDoubleTapped?.Invoke(i); }
        };
    }

    public IReadOnlyList<VideoTileViewModel>? Tiles { get => GetValue(TilesProperty); set => SetValue(TilesProperty, value); }

    /// <summary>Index into <see cref="Tiles"/> of the spotlighted tile, or -1.</summary>
    public int Spotlight { get => GetValue(SpotlightProperty); set => SetValue(SpotlightProperty, value); }

    /// <summary>Index of the tile shown alone over the whole area, or -1.</summary>
    public int Fullscreen { get => GetValue(FullscreenProperty); set => SetValue(FullscreenProperty, value); }

    public FormFactor Form { get => GetValue(FormProperty); set => SetValue(FormProperty, value); }

    /// <summary>Animate layout changes over 150 ms (desktop only).</summary>
    public bool Animate { get => GetValue(AnimateProperty); set => SetValue(AnimateProperty, value); }

    public IBrush? Accent { get => GetValue(AccentProperty); set => SetValue(AccentProperty, value); }

    public event Action<int>? TileTapped;
    public event Action<int>? TileDoubleTapped;

    /// <summary>The rectangles the tiles are laid out in right now (final positions, not mid-animation); for tests and screenshots.</summary>
    public IReadOnlyList<CoreRect> TargetRects => _target;

    // The subscriptions to the (long-lived) view model are only held while the control is in the visual tree, so a page that was left
    // (a new view is created per navigation) does not stay alive and keep drawing.
    private IReadOnlyList<VideoTileViewModel> _hooked = [];
    private INotifyCollectionChanged? _hookedSource;
    private bool _attached;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        Hook();
        Relayout(false);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        Unhook();
    }

    private void Hook()
    {
        Unhook();
        _hooked = _tiles.ToList();
        foreach (var t in _hooked) { t.Changed += InvalidateVisual; }
        _hookedSource = Tiles as INotifyCollectionChanged;
        if (_hookedSource is not null) { _hookedSource.CollectionChanged += OnCollectionChanged; }
    }

    private void Unhook()
    {
        foreach (var t in _hooked) { t.Changed -= InvalidateVisual; }
        _hooked = [];
        _labelCache.Clear();
        if (_hookedSource is not null) { _hookedSource.CollectionChanged -= OnCollectionChanged; }
        _hookedSource = null;
    }

    private void OnTilesChanged(IReadOnlyList<VideoTileViewModel>? old, IReadOnlyList<VideoTileViewModel>? value)
    {
        _tiles = value ?? [];
        if (_attached) { Hook(); }
        Relayout(false);
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _tiles = Tiles ?? [];
        // the collection is the same instance; only the tiles in it changed
        foreach (var t in _hooked) { t.Changed -= InvalidateVisual; }
        _hooked = _tiles.ToList();
        foreach (var t in _hooked) { t.Changed += InvalidateVisual; }
        _labelCache.Clear();
        Relayout(false);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        Relayout(false);
    }

    private IEnumerable<int> VisibleIndexes()
    {
        if (Fullscreen >= 0 && Fullscreen < _tiles.Count) { return [Fullscreen]; }
        return Enumerable.Range(0, _tiles.Count);
    }

    private void Relayout(bool allowAnimation)
    {
        var area = new CoreArea(Bounds.Width, Bounds.Height);
        var indexes = VisibleIndexes().ToArray();
        var full = Fullscreen >= 0 && Fullscreen < _tiles.Count;
        var spot = !full && Spotlight >= 0 && Spotlight < _tiles.Count && _tiles.Count > 1 ? Spotlight : (int?)null;
        var rects = VideoLayout.Compute(area, indexes.Select(i => _tiles[i].Tile).ToArray(), Form, spot);
        // rects are in visible order; expand to tile order (hidden tiles get an empty rect)
        var all = new CoreRect[_tiles.Count];
        for (var k = 0; k < indexes.Length; k++) { all[indexes[k]] = rects[k]; }

        var canAnimate = allowAnimation && Animate && _from.Length == all.Length && _target.Length == all.Length && area == _lastArea;
        if (canAnimate)
        {
            _from = (CoreRect[])CurrentRects().Clone(); // CurrentRects reuses its array
            _animStart = Stopwatch.GetTimestamp();
            _animating = true;
        }
        else
        {
            _from = all;
            _animating = false;
        }
        _target = all;
        _lastArea = area;
        InvalidateVisual();
    }

    private double Progress()
    {
        if (!_animating) { return 1; }
        var ms = (Stopwatch.GetTimestamp() - _animStart) * 1000.0 / Stopwatch.Frequency;
        var t = Math.Clamp(ms / AnimationMs, 0, 1);
        if (t >= 1) { _animating = false; }
        return 1 - Math.Pow(1 - t, 3);
    }

    /// <summary>The rectangles to draw now: <see cref="_target"/> itself when not animating, else an interpolation in a reused array.</summary>
    private CoreRect[] CurrentRects()
    {
        var p = Progress();
        if (p >= 1) { return _target; }
        if (_scratch.Length != _target.Length) { _scratch = new CoreRect[_target.Length]; }
        for (var i = 0; i < _scratch.Length; i++)
        {
            _scratch[i] = Lerp(_from[i], _target[i], p);
        }
        return _scratch;
    }

    private static CoreRect Lerp(CoreRect a, CoreRect b, double t) =>
        new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Width + (b.Width - a.Width) * t, a.Height + (b.Height - a.Height) * t);

    private int? HitTile(Point p)
    {
        foreach (var i in VisibleIndexes())
        {
            var r = _target[i];
            if (p.X >= r.X && p.X <= r.Right && p.Y >= r.Y && p.Y <= r.Bottom) { return i; }
        }
        return null;
    }

    public override void Render(DrawingContext context)
    {
        context.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size));
        if (_tiles.Count == 0 || _target.Length != _tiles.Count) { return; }
        var rects = CurrentRects();
        if (_animating)
        {
            Avalonia.Threading.DispatcherTimer.RunOnce(InvalidateVisual, TimeSpan.FromMilliseconds(16), Avalonia.Threading.DispatcherPriority.Render);
        }
        var radius = Form is FormFactor.PhonePortrait or FormFactor.PhoneLandscape ? 8 : 12;
        var full = Fullscreen >= 0 && Fullscreen < _tiles.Count;
        var end = full ? Fullscreen + 1 : _tiles.Count;
        for (var i = full ? Fullscreen : 0; i < end; i++)
        {
            var r = rects[i];
            if (r.Width < 2 || r.Height < 2) { continue; }
            DrawTile(context, _tiles[i], new Rect(r.X, r.Y, r.Width, r.Height), radius, i == Spotlight && _tiles.Count > 1 && Fullscreen < 0);
        }
    }

    private void DrawTile(DrawingContext context, VideoTileViewModel tile, Rect rect, double radius, bool spotlight)
    {
        var rounded = new RoundedRect(rect, radius);
        using (context.PushClip(rounded))
        {
            context.DrawRectangle(TileBack, null, rect);
            if (tile.Frame is { } bmp && !tile.BeforeStart)
            {
                DrawFrame(context, bmp, tile.Rotation, rect);
            }
            else if (tile.Poster is { } poster && tile.PosterAllowed)
            {
                var s = Math.Max(rect.Width / poster.Size.Width, rect.Height / poster.Size.Height);
                var w = poster.Size.Width * s;
                var h = poster.Size.Height * s;
                context.DrawImage(poster, new Rect(poster.Size), new Rect(rect.X + (rect.Width - w) / 2, rect.Y + (rect.Height - h) / 2, w, h));
                if (tile.BeforeStart) { context.DrawRectangle(PosterShade, null, rect); }
            }
            else if (VideoOffIcon() is { } icon)
            {
                var size = Math.Clamp(Math.Min(rect.Width, rect.Height) * 0.3, 20, 56);
                var m = Matrix.CreateScale(size / 24, size / 24) * Matrix.CreateTranslation(rect.Center.X - size / 2, rect.Center.Y - size / 2 - (tile.Unavailable ? 14 : 0));
                using (context.PushTransform(m)) { context.DrawGeometry(IconBrush, null, icon); }
            }

            if (!tile.HasFrame && tile.Unavailable)
            {
                DrawCentered(context, rect, tile.Problem ?? "Video playback is not available on this device yet", rect.Height < 120 ? 10 : 12);
            }
        }

        // name label, bottom left
        var label = tile.Name;
        if (rect.Width >= 60 && rect.Height >= 40)
        {
            var maxWidth = Math.Max(10, Math.Round(rect.Width - 24)); // whole dips: an animation does not rebuild the text every frame
            if (!_labelCache.TryGetValue(tile, out var cached) || cached.Width != maxWidth)
            {
                cached = (maxWidth, new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelFont, 11, Brushes.White)
                {
                    MaxTextWidth = maxWidth,
                    MaxLineCount = 1,
                    Trimming = TextTrimming.CharacterEllipsis,
                });
                _labelCache[tile] = cached;
            }
            var text = cached.Text;
            var chip = new Rect(rect.X + 6, rect.Bottom - text.Height - 12, text.Width + 12, text.Height + 6);
            context.DrawRectangle(LabelBack, null, chip, 6, 6);
            context.DrawText(text, new Point(chip.X + 6, chip.Y + 3));
        }
        if (spotlight && Accent is { } accent)
        {
            if (_accentPen is null || !ReferenceEquals(accent, _accentBrush))
            {
                _accentBrush = accent;
                _accentPen = new Pen(accent, 2.5);
            }
            context.DrawRectangle(null, _accentPen, rect.Deflate(1.25), radius, radius);
        }
    }

    private Geometry? VideoOffIcon()
    {
        if (_videoOff is null && Application.Current?.TryFindResource("IconVideoOff", out var g) == true && g is Geometry icon) { _videoOff = icon; }
        return _videoOff;
    }

    private static void DrawFrame(DrawingContext context, Avalonia.Media.Imaging.Bitmap bmp, int rotation, Rect rect)
    {
        double w = bmp.PixelSize.Width, h = bmp.PixelSize.Height;
        var swap = rotation % 180 != 0;
        var dw = swap ? h : w;
        var dh = swap ? w : h;
        var s = Math.Max(rect.Width / dw, rect.Height / dh); // the tile has the frame's display aspect: cover = fit, without hairlines
        var m = Matrix.CreateTranslation(-w / 2, -h / 2) * Matrix.CreateRotation(rotation * Math.PI / 180) * Matrix.CreateScale(s, s)
                * Matrix.CreateTranslation(rect.Center.X, rect.Center.Y);
        using (context.PushTransform(m))
        {
            context.DrawImage(bmp, new Rect(0, 0, w, h));
        }
    }

    private static void DrawCentered(DrawingContext context, Rect rect, string message, double size)
    {
        var text = new FormattedText(message, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelFont, size,
            new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)))
        {
            MaxTextWidth = Math.Max(10, rect.Width - 24),
            MaxTextHeight = Math.Max(10, rect.Height - 8),
            TextAlignment = TextAlignment.Center,
        };
        context.DrawText(text, new Point(rect.X + (rect.Width - text.MaxTextWidth) / 2, rect.Center.Y - text.Height / 2 + 14));
    }
}
