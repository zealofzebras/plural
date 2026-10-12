using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Plural.App.ViewModels;

namespace Plural.App.Views;

public partial class TakesView : UserControl
{
    /// <summary>Width from which the page shows the detail pane next to the list.</summary>
    private const double WideFrom = 840;

    public TakesView()
    {
        InitializeComponent();
        SizeChanged += (_, e) => ApplyWidth(e.NewSize.Width);
    }

    private TakesViewModel? Vm => DataContext as TakesViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        ApplyWidth(Bounds.Width);
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        ApplyWidth(Bounds.Width);
    }

    private void ApplyWidth(double width)
    {
        if (Vm is { } vm && width > 0)
        {
            vm.IsWide = width >= WideFrom;
        }
    }

    private static TakeViewModel? TakeOf(object? sender) => (sender as Control)?.DataContext as TakeViewModel;

    /// <summary>A tap on the … button (or any button in the row) belongs to that button, not to the row.</summary>
    private static bool FromButton(RoutedEventArgs e) =>
        e.Source is Visual v && (v is Button || v.FindAncestorOfType<Button>() is not null);

    private void OnItemTapped(object? sender, TappedEventArgs e)
    {
        if (FromButton(e) || TakeOf(sender) is not { } take)
        {
            return;
        }
        Vm?.Activate(take);
    }

    private void OnItemDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (FromButton(e) || TakeOf(sender) is not { } take || Vm is not { IsSelecting: false } vm)
        {
            return;
        }
        vm.Open(take);
    }

    /// <summary>Long-press on touch.</summary>
    private void OnItemHolding(object? sender, HoldingRoutedEventArgs e)
    {
        if (e.HoldingState == HoldingState.Started && TakeOf(sender) is { } take)
        {
            e.Handled = true;
            Vm?.ShowMenu(take);
        }
    }

    /// <summary>Right-click (and the menu key).</summary>
    private void OnItemContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (TakeOf(sender) is { } take)
        {
            e.Handled = true;
            Vm?.ShowMenu(take);
        }
    }

    private void OnScrimTapped(object? sender, TappedEventArgs e) => Vm?.HandleBack();
}
