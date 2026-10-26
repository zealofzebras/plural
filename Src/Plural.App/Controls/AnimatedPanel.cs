using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Plural.App.Controls;

/// <summary>Keeps its content visible until the closing fade/slide finishes.</summary>
public sealed class AnimatedPanel : Grid
{
    public static readonly StyledProperty<bool> IsOpenProperty =
        AvaloniaProperty.Register<AnimatedPanel, bool>(nameof(IsOpen));

    public static readonly StyledProperty<Vector> SlideOffsetProperty =
        AvaloniaProperty.Register<AnimatedPanel, Vector>(nameof(SlideOffset), new Vector(0, 12));

    public static readonly StyledProperty<Control?> SlideTargetProperty =
        AvaloniaProperty.Register<AnimatedPanel, Control?>(nameof(SlideTarget));

    private readonly TranslateTransform _slide = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Stopwatch _elapsed = new();
    private static readonly CubicEaseOut Easing = new();
    private double _fromOpacity;
    private Vector _fromSlide;

    public bool IsOpen
    {
        get => GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    public Vector SlideOffset
    {
        get => GetValue(SlideOffsetProperty);
        set => SetValue(SlideOffsetProperty, value);
    }

    public Control? SlideTarget
    {
        get => GetValue(SlideTargetProperty);
        set => SetValue(SlideTargetProperty, value);
    }

    public AnimatedPanel()
    {
        IsVisible = false;
        IsHitTestVisible = false;
        Opacity = 0;
        RenderTransform = _slide;
        _timer.Tick += OnTick;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsOpenProperty)
        {
            if (TopLevel.GetTopLevel(this) is not null) { Animate(); }
            else { SetFinalState(); }
        }
        else if (change.Property == SlideTargetProperty)
        {
            if (change.OldValue is Control oldTarget) { oldTarget.RenderTransform = null; }
            RenderTransform = SlideTarget is null ? _slide : null;
            if (SlideTarget is { } target) { target.RenderTransform = _slide; }
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (IsOpen)
        {
            IsVisible = false;
            Opacity = 0;
            Animate();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop();
        SetFinalState();
        base.OnDetachedFromVisualTree(e);
    }

    private void SetFinalState()
    {
        IsVisible = IsOpen;
        IsHitTestVisible = IsOpen;
        Opacity = IsOpen ? 1 : 0;
        _slide.X = IsOpen ? 0 : SlideOffset.X;
        _slide.Y = IsOpen ? 0 : SlideOffset.Y;
    }

    private void Animate()
    {
        _fromOpacity = Opacity;
        _fromSlide = IsVisible ? new Vector(_slide.X, _slide.Y) : SlideOffset;
        IsVisible = true;
        IsHitTestVisible = IsOpen;
        _elapsed.Restart();
        OnTick(this, EventArgs.Empty);
        _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var progress = Math.Clamp(_elapsed.Elapsed.TotalMilliseconds / 320, 0, 1);
        if (progress == 1)
        {
            _timer.Stop();
            SetFinalState();
            return;
        }
        var eased = Easing.Ease(progress);
        var target = IsOpen ? default : SlideOffset;
        Opacity = _fromOpacity + ((IsOpen ? 1 : 0) - _fromOpacity) * eased;
        _slide.X = _fromSlide.X + (target.X - _fromSlide.X) * eased;
        _slide.Y = _fromSlide.Y + (target.Y - _fromSlide.Y) * eased;
    }
}
