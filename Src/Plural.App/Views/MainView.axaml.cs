using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using SmartMike.App.ViewModels;

namespace SmartMike.App.Views;

public partial class MainView : UserControl
{
    private const double CompactBelow = 720;
    private const double SidePaneWidth = 400;
    private const double CardMargin = 6;
    private const double BodyMargin = 12;
    private bool? _compact;
    private IActivatableLifetime? _activatable;

    public MainView()
    {
        InitializeComponent();
        SizeChanged += (_, e) => ApplyLayout(e.NewSize.Width);
        PreviewPanel.SizeChanged += (_, e) =>
        {
            var h = Math.Round(e.NewSize.Width * 9 / 16);
            if (Math.Abs(PreviewPanel.Height - h) > 0.5 && e.NewSize.Width > 0)
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
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_activatable is not null)
        {
            _activatable.Activated -= OnActivated;
            _activatable.Deactivated -= OnDeactivated;
            _activatable = null;
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

    /// <summary>
    /// Phone: one column (preview, mics, takes). Wide: preview and takes in a 400 dp side column, mic cards fill the rest.
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

        double cardsArea;
        if (compact)
        {
            if (_compact != true)
            {
                Body.ColumnDefinitions = new ColumnDefinitions("*");
                Grid.SetColumn(PreviewPanel, 0);
                Grid.SetRow(PreviewPanel, 1);
                Grid.SetColumn(CardsPanel, 0);
                Grid.SetRow(CardsPanel, 2);
                Grid.SetRowSpan(CardsPanel, 1);
                Grid.SetColumn(TakesPanel, 0);
                Grid.SetRow(TakesPanel, 3);
            }
            cardsArea = width - 2 * BodyMargin;
        }
        else
        {
            if (_compact != false)
            {
                Body.ColumnDefinitions = new ColumnDefinitions($"{SidePaneWidth},*");
                Grid.SetColumn(PreviewPanel, 0);
                Grid.SetRow(PreviewPanel, 1);
                Grid.SetColumn(TakesPanel, 0);
                Grid.SetRow(TakesPanel, 2);
                Grid.SetColumn(CardsPanel, 1);
                Grid.SetRow(CardsPanel, 1);
                Grid.SetRowSpan(CardsPanel, 3);
            }
            cardsArea = width - 2 * BodyMargin - SidePaneWidth;
        }
        _compact = compact;

        // Each card takes (cardsArea / n) minus its margins, where n is the number of cards that fit at >= 300 dp.
        var perCard = 2 * CardMargin;
        var n = Math.Max(1, (int)Math.Floor(cardsArea / (300 + perCard)));
        var cardWidth = Math.Min(480, cardsArea / n - perCard - 0.5);
        Resources["CardWidth"] = Math.Max(220, cardWidth);
    }
}
