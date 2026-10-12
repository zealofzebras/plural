using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Plural.App.ViewModels;
using Plural.Core.Playback;

namespace Plural.App.Views;

public partial class PlayerView : UserControl
{
    private PlayerViewModel? _vm;
    private ScrollViewer? _rowsScroll;
    private bool _splitting;
    private double _splitStartY, _splitStartHeight, _splitFraction = 0.5;

    public PlayerView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Relayout();
        HeaderGrid.SizeChanged += (_, _) => Relayout();
        RecBar.SizeChanged += (_, _) => Relayout();
        TransportBar.SizeChanged += (_, _) => Relayout();
        Timeline.SizeChanged += (_, e) => _vm?.SetWaveWidth(e.NewSize.Width);
        Timeline.SeekRequested += (t, final) => _vm?.Scrub(t, final);
        Timeline.ViewRequested += (start, span) => _vm?.SetView(start, span);
        Timeline.VerticalScrollRequested += dy =>
        {
            if (_rowsScroll is { } sv)
            {
                sv.Offset = new Vector(0, Math.Clamp(sv.Offset.Y + dy, 0, Math.Max(0, sv.Extent.Height - sv.Viewport.Height)));
            }
        };
        Minimap.StartRequested += start => _vm?.SetView(start, _vm.ViewSpan);
        Stage.TileTapped += i => _vm?.TileTapped(i);
        Stage.TileDoubleTapped += i => _vm?.TileDoubleTapped(i);
        RowsList.TemplateApplied += (_, e) => _rowsScroll = e.NameScope.Find<ScrollViewer>("RowsScroll");
        AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Unsubscribe();
        _vm = DataContext as PlayerViewModel;
        if (_vm is not null)
        {
            if (_attached) { _vm.PropertyChanged += OnVmPropertyChanged; }
            _splitFraction = _vm.Main.PlayerSplitter;
            Relayout();
        }
    }

    // The view model lives as long as the app but a view only as long as its page: subscribe while attached to the visual tree.
    private bool _attached;

    private void Unsubscribe()
    {
        if (_vm is not null) { _vm.PropertyChanged -= OnVmPropertyChanged; }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged; // never twice
            _vm.PropertyChanged += OnVmPropertyChanged;
            Relayout();
        }
        Dispatcher.UIThread.Post(() => Focus(), DispatcherPriority.Loaded);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        Unsubscribe();
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerViewModel.HasVideo):
            case nameof(PlayerViewModel.FocusVideo):
            case nameof(PlayerViewModel.FullscreenIndex):
            case nameof(PlayerViewModel.Form):
                Relayout();
                break;
            case nameof(PlayerViewModel.IsEditingTitle) when _vm?.IsEditingTitle == true:
                Dispatcher.UIThread.Post(() => { TitleBox.Focus(); TitleBox.SelectAll(); }, DispatcherPriority.Input);
                break;
            case nameof(PlayerViewModel.HeaderWidth):
                TracksGrid.ColumnDefinitions[0].Width = new GridLength(_vm!.HeaderWidth);
                break;
        }
    }

    /// <summary>Form factor from the page size, then the height of the video area (VideoLayout's budget), then the header column.</summary>
    private void Relayout()
    {
        if (_vm is not { } vm || Bounds.Width <= 0 || Bounds.Height <= 0) { return; }
        vm.SetViewport(Bounds.Width, Bounds.Height);
        TracksGrid.ColumnDefinitions[0].Width = new GridLength(vm.HeaderWidth);
        var top = (HeaderGrid.IsVisible ? HeaderGrid.Bounds.Height + HeaderGrid.Margin.Top + HeaderGrid.Margin.Bottom : 0)
                  + (RecBar.IsVisible ? RecBar.Bounds.Height : 0);
        var transport = TransportBar.Bounds.Height;
        var splitter = vm.ShowSplitter ? Splitter.Bounds.Height : 0;
        double stage;
        if (vm.IsFullscreen)
        {
            stage = Math.Max(0, Bounds.Height - top - transport);
        }
        else
        {
            // The stage row also holds the host's margins: count them in the budget (the rest of the page is video area + transport),
            // otherwise "Focus video" (video area = everything left) pushes the compact transport bar off the bottom.
            var margins = vm.HasVideo ? StageHost.Margin.Top + StageHost.Margin.Bottom : 0;
            stage = VideoLayout.VideoAreaHeight(vm.Form, Bounds.Height, top, transport + splitter + margins, vm.HasVideo, vm.FocusVideo, _splitFraction);
            if (vm.HasVideo) { stage += margins; }
        }
        var rows = Root.RowDefinitions;
        if (Math.Abs(rows[2].Height.Value - stage) > 0.5 || rows[2].Height.GridUnitType != GridUnitType.Pixel)
        {
            rows[2].Height = new GridLength(stage);
        }
    }

    // ------------------------------------------------------------------ splitter

    private void OnSplitterPressed(object? sender, PointerPressedEventArgs e)
    {
        _splitting = true;
        _splitStartY = e.GetPosition(this).Y;
        _splitStartHeight = Root.RowDefinitions[2].Height.Value;
        e.Pointer.Capture((IInputElement?)sender);
        e.Handled = true;
    }

    private void OnSplitterMoved(object? sender, PointerEventArgs e)
    {
        if (!_splitting || Bounds.Height <= 0) { return; }
        var h = _splitStartHeight + e.GetPosition(this).Y - _splitStartY;
        _splitFraction = Math.Clamp(h / Bounds.Height, 0.15, 0.5);
        Relayout();
    }

    private void OnSplitterReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_splitting) { return; }
        _splitting = false;
        e.Pointer.Capture(null);
        _vm?.Main.SetPlayerSplitter(_splitFraction);
    }

    // ------------------------------------------------------------------ rows

    private static TrackRowViewModel? RowOf(object? sender) => (sender as Control)?.DataContext as TrackRowViewModel;

    private void OnRowHeaderTapped(object? sender, TappedEventArgs e)
    {
        if (RowOf(sender) is { } row) { _vm?.FocusRow(row); }
    }

    /// <summary>Long-press on a phone row header opens its gain slider.</summary>
    private void OnRowHeaderHolding(object? sender, HoldingRoutedEventArgs e)
    {
        if (e.HoldingState == HoldingState.Started && RowOf(sender) is { IsCompact: true } row)
        {
            e.Handled = true;
            row.IsGainOpen = !row.IsGainOpen;
        }
    }

    private void OnRowHeaderContext(object? sender, ContextRequestedEventArgs e)
    {
        if (RowOf(sender) is { IsCompact: true } row)
        {
            e.Handled = true;
            row.IsGainOpen = !row.IsGainOpen;
        }
    }

    private void OnGainDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (RowOf(sender) is { } row) { row.GainDb = 0; }
    }

    // ------------------------------------------------------------------ title, dialogs

    private void OnTitleKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _vm?.CommitTitleCommand.Execute(null);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _vm?.CancelTitleEditCommand.Execute(null);
        }
    }

    private void OnTitleLostFocus(object? sender, RoutedEventArgs e)
    {
        if (_vm?.IsEditingTitle == true) { _vm.CommitTitleCommand.Execute(null); }
    }

    private void OnScrimTapped(object? sender, TappedEventArgs e) => _vm?.HandleBack();

    // ------------------------------------------------------------------ keys (desktop)

    private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (_vm is not { } vm || e.Source is TextBox or Avalonia.Controls.Primitives.RangeBase || vm.IsNotesOpen || vm.IsMenuOpen) { return; }
        if (e.KeyModifiers != KeyModifiers.None) { return; }
        switch (e.Key)
        {
            case Key.Space: vm.TogglePlayCommand.Execute(null); break;
            case Key.Left: vm.SeekTo(vm.PositionSeconds - 5); break;
            case Key.Right: vm.SeekTo(vm.PositionSeconds + 5); break;
            case Key.Home: vm.SeekTo(0); break;
            case Key.End: vm.SeekTo(vm.DurationSeconds); break;
            case Key.M: vm.FocusedRow?.ToggleMuteCommand.Execute(null); break;
            case Key.S: vm.FocusedRow?.ToggleSoloCommand.Execute(null); break;
            case Key.F: vm.ToggleFullscreenCommand.Execute(null); break;
            default: return;
        }
        e.Handled = true;
    }
}
