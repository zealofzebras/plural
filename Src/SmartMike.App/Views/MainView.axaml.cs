using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using SmartMike.App.ViewModels;

namespace SmartMike.App.Views;

public partial class MainView : UserControl
{
    private const double CardWidth = 300;
    private const double CardMargin = 8;

    public MainView()
    {
        InitializeComponent();
        Cards.SizeChanged += (_, e) => UpdateCardWidth(e.NewSize.Width);
    }

    protected override async void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (DataContext is MainViewModel vm)
        {
            await vm.InitializeAsync();
        }
    }

    /// <summary>Fixed-width cards in a wrap panel; on a screen too narrow for two, one card fills the width.</summary>
    private void UpdateCardWidth(double available)
    {
        // ItemsControl margin (8 each side) + card margins (8 each side).
        var inner = available - 2 * CardMargin;
        var width = inner < 2 * (CardWidth + 2 * CardMargin)
            ? Math.Max(200, inner - 2 * CardMargin)
            : CardWidth;
        Resources["CardWidth"] = width;
    }
}
