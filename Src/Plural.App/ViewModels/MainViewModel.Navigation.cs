using System;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Plural.App.Services;
using Plural.Core.Library;

namespace Plural.App.ViewModels;

public enum AppPage
{
    Recorder,
    Takes,
    Player,
}

// Page navigation (Recorder -> Takes -> Player) and the take library of the recording root.
public partial class MainViewModel
{
    private TakeLibrary? _library;
    private string? _libraryRoot;
    private bool _libraryMaintained;

    /// <summary>Which page is on top. Recording and the mic UI keep running underneath other pages.</summary>
    [ObservableProperty] public partial AppPage CurrentPage { get; set; } = AppPage.Recorder;

    /// <summary>The view model of the page on top (null on the recorder page); MainView picks the view with a DataTemplate per type.</summary>
    [ObservableProperty] public partial ViewModelBase? PageViewModel { get; set; }

    public TakesViewModel Takes { get; private set; } = null!;
    public PlayerViewModel Player { get; private set; } = null!;

    public bool ShowToastShare => CanShare && HasToastTake;
    public bool ShowToastFolder => CanOpenFolder && HasToastTake;

    partial void OnHasToastTakeChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowToastShare));
        OnPropertyChanged(nameof(ShowToastFolder));
    }

    public bool IsRecorderPage => CurrentPage == AppPage.Recorder;
    public bool IsNotRecorderPage => CurrentPage != AppPage.Recorder;

    internal IAppHost? Host => _host;
    internal Plural.Core.Session.MikeHub? Hub => _hub;

    /// <summary>Player page: video area as a fraction of the page height (the splitter), remembered in the settings.</summary>
    public double PlayerSplitter { get; private set; } = 0.5;

    public void SetPlayerSplitter(double fraction)
    {
        PlayerSplitter = Math.Clamp(fraction, 0.15, 0.5);
        SaveSettings();
    }

    private void CreatePages()
    {
        Takes = new TakesViewModel(this);
        Player = new PlayerViewModel(this);
    }

    partial void OnCurrentPageChanged(AppPage value)
    {
        if (value != AppPage.Player) { Player.Unload(); }
        PageViewModel = value switch
        {
            AppPage.Takes => Takes,
            AppPage.Player => Player,
            _ => null,
        };
        OnPropertyChanged(nameof(IsRecorderPage));
        OnPropertyChanged(nameof(IsNotRecorderPage));
    }

    /// <summary>The library of the current recording root; one instance per root (the root can be changed in the settings).</summary>
    public TakeLibrary Library
    {
        get
        {
            if (_library is null || !string.Equals(_libraryRoot, RecordingRoot, StringComparison.Ordinal))
            {
                _libraryRoot = RecordingRoot;
                _library = new TakeLibrary(RecordingRoot);
            }
            return _library;
        }
    }

    /// <summary>Once per start, off the UI thread: purge the trash (30 days) and mark interrupted takes as recovered.</summary>
    private Task MaintainLibraryAsync()
    {
        if (_libraryMaintained)
        {
            return Task.CompletedTask;
        }
        _libraryMaintained = true;
        var lib = Library;
        return Task.Run(() =>
        {
            try
            {
                lib.PurgeTrash(TimeSpan.FromDays(30));
                lib.RecoverInterrupted(_hub?.RecordingFolder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // read-only or vanished storage: the page still lists what it can
            }
        });
    }

    public async Task LoadTakesAsync()
    {
        await MaintainLibraryAsync();
        await Takes.RefreshAsync();
    }

    [RelayCommand]
    private void OpenTakes()
    {
        CurrentPage = AppPage.Takes;
        _ = LoadTakesAsync();
    }

    [RelayCommand]
    private void ViewTakes()
    {
        IsToastVisible = false;
        OpenTakes();
    }

    /// <summary>Opens a take in the player. A take that is still recording cannot be played.</summary>
    public void OpenPlayer(TakeInfo take)
    {
        if (take.Status == TakeStatus.Recording)
        {
            ShowToast("Still recording", "Stop the recording to play this take.", false, null);
            return;
        }
        Player.Load(take);
        CurrentPage = AppPage.Player;
    }

    /// <summary>
    /// Back: closes an open sheet or dialog, else goes one page back (Player to Takes to Recorder).
    /// Returns false on the recorder page with nothing open, so the platform can handle it (Android: leave the app).
    /// </summary>
    public bool GoBack()
    {
        switch (CurrentPage)
        {
            case AppPage.Player:
                if (Player.HandleBack()) { return true; }
                CurrentPage = AppPage.Takes;
                _ = Takes.RefreshAsync();
                return true;
            case AppPage.Takes:
                if (!Takes.HandleBack())
                {
                    CurrentPage = AppPage.Recorder;
                }
                return true;
        }
        if (IsMenuOpen) { CloseMenu(); return true; }
        if (IsSettingsOpen) { CloseSettings(); return true; }
        if (IsAddMicOpen) { CloseAddMic(); return true; }
        if (IsDevicesOpen) { CloseDevices(); return true; }
        if (IsAboutOpen) { CloseAbout(); return true; }
        return false;
    }

    [RelayCommand]
    private void NavigateBack() => GoBack();
}
