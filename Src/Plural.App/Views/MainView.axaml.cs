using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Plural.App.ViewModels;

namespace Plural.App.Views;

public partial class MainView : UserControl
{
    private const double CompactBelow = 720;
    private const double SidePaneWidth = 400;
    private const double CardMargin = 6;
    private const double BodyMargin = 12;
    private IActivatableLifetime? _activatable;
    private TopLevel? _top;

    public MainView()
    {
        InitializeComponent();
        SizeChanged += (_, e) => ApplyLayout(e.NewSize.Width);
        PreviewPanel.SizeChanged += (_, e) =>
        {
            var h = Math.Round(e.NewSize.Width * 9 / 16);
            if (e.NewSize.Width > 0 && (double.IsNaN(PreviewPanel.Height) || Math.Abs(PreviewPanel.Height - h) > 0.5))
            {
                PreviewPanel.Height = h;
            }
        };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _activatable = Application.Current?.TryGetFeature<IActivatableLifetime>();
        if (_activatable is not null)
        {
            _activatable.Activated += OnActivated;
            _activatable.Deactivated += OnDeactivated;
        }
        // Back: Android back gesture/button, Escape, Alt+Left (tunnel, so it works wherever the focus is).
        _top = TopLevel.GetTopLevel(this);
        if (_top is not null)
        {
            _top.BackRequested += OnBackRequested;
            _top.AddHandler(KeyDownEvent, OnTopKeyDown, RoutingStrategies.Tunnel);
        }
    }

    private void OnBackRequested(object? sender, RoutedEventArgs e)
    {
        if (_vm?.GoBack() == true)
        {
            e.Handled = true;
        }
    }

    private void OnTopKeyDown(object? sender, KeyEventArgs e)
    {
        var back = e.Key == Key.Escape || (e.Key == Key.Left && e.KeyModifiers == KeyModifiers.Alt) || e.Key == Key.BrowserBack;
        if (back && _vm?.GoBack() == true)
        {
            e.Handled = true;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_activatable is not null)
        {
            _activatable.Activated -= OnActivated;
            _activatable.Deactivated -= OnDeactivated;
            _activatable = null;
        }
        if (_top is not null)
        {
            _top.BackRequested -= OnBackRequested;
            _top.RemoveHandler(KeyDownEvent, OnTopKeyDown);
            _top = null;
        }
        base.OnDetachedFromVisualTree(e);
    }

    // The camera only runs while the app is visible: Background means the app left the foreground (Android).
    private void OnActivated(object? sender, ActivatedEventArgs e) => (DataContext as MainViewModel)?.SetAppVisible(true);

    private void OnDeactivated(object? sender, ActivatedEventArgs e)
    {
        if (e.Kind == ActivationKind.Background)
        {
            (DataContext as MainViewModel)?.SetAppVisible(false);
        }
    }

    protected override async void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        ApplyLayout(Bounds.Width);
        if (DataContext is MainViewModel vm)
        {
            await vm.InitializeAsync();
        }
    }

    private void OnScrimTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.CloseSettingsCommand.Execute(null);
        }
    }

    private void OnAboutScrimTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.CloseAboutCommand.Execute(null);
        }
    }

    private void OnDevicesScrimTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.CloseDevicesCommand.Execute(null);
        }
    }

    private void OnAddMicScrimTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.CloseAddMicCommand.Execute(null);
        }
    }

    private void OnMenuScrimTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.CloseMenuCommand.Execute(null);
        }
    }

    private MainViewModel? _vm;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
        }
        _vm = DataContext as MainViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmPropertyChanged;
            ApplyChrome();
            Relayout();
        }
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.ImmersiveActive):
                ApplyChrome();
                break;
            case nameof(MainViewModel.IsRecordingUi):
                Root.Classes.Set("recui", _vm?.IsRecordingUi == true);
                break;
            case nameof(MainViewModel.ShowPreviewPanel):
                Relayout();
                break;
        }
    }

    /// <summary>Immersive video floats the UI over the camera, so the subtree is forced dark for readable light content.</summary>
    private void ApplyChrome()
    {
        var immersive = _vm?.ImmersiveActive == true;
        Root.Classes.Set("immersive", immersive);
        Scope.RequestedThemeVariant = immersive ? ThemeVariant.Dark : ThemeVariant.Default;
    }

    private void Relayout()
    {
        _layoutKey = null;
        ApplyLayout(Bounds.Width);
    }

    private string? _layoutKey;

    /// <summary>
    /// Phone (or no side preview): one column. Wide with the preview panel: preview in a 400 dp side column, mic cards fill the rest.
    /// Cards get a width so that they fill the row evenly.
    /// </summary>
    private void ApplyLayout(double width)
    {
        if (width <= 0)
        {
            return;
        }
        var compact = width < CompactBelow;
        Root.Classes.Set("compact", compact);
        Root.Classes.Set("short", Bounds.Height > 0 && Bounds.Height < 520);

        var side = !compact && _vm?.ShowPreviewPanel == true;
        var key = $"{compact}|{side}";
        if (key != _layoutKey)
        {
            _layoutKey = key;
            Grid.SetRow(PreviewPanel, 1);
            Grid.SetColumn(PreviewPanel, 0);
            if (side)
            {
                Body.ColumnDefinitions = new ColumnDefinitions($"{SidePaneWidth},*");
                Grid.SetColumn(CardsPanel, 1);
                Grid.SetRow(CardsPanel, 1);
                Grid.SetRowSpan(CardsPanel, 2);
            }
            else
            {
                Body.ColumnDefinitions = new ColumnDefinitions("*");
                Grid.SetColumn(CardsPanel, 0);
                Grid.SetRow(CardsPanel, 2);
                Grid.SetRowSpan(CardsPanel, 1);
            }
        }

        var cardsArea = width - 2 * BodyMargin - (side ? SidePaneWidth : 0);

        // Each card takes (cardsArea / n) minus its margins, where n is the number of cards that fit at >= 300 dp.
        var perCard = 2 * CardMargin;
        var n = Math.Max(1, (int)Math.Floor(cardsArea / (300 + perCard)));
        var cardWidth = Math.Min(480, cardsArea / n - perCard - 0.5);
        Resources["CardWidth"] = Math.Max(220, cardWidth);

        // Compact recording strip: stacked rows on phones, a row of small meters on wide screens.
        Resources["StripWidth"] = compact ? Math.Max(180, width - 24 - 16 - 12) : 240.0;
    }

}
