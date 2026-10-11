using System;
using System.Reflection;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Plural.App.ViewModels;

// About page: version, this device, website and open-source credits.
public partial class MainViewModel
{
    public const string WebsiteUrl = "https://getplural.net";

    [ObservableProperty] public partial bool IsAboutOpen { get; set; }

    public string AppVersionText => $"Version {Version}";

    public string PlatformText =>
        $"{_host?.PlatformName ?? RuntimeInformation.OSDescription} · {RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()} · .NET {Environment.Version}";

    public string CopyrightText =>
        typeof(MainViewModel).Assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? "getplural.net";

    public string CreditsText =>
        "Avalonia UI and CommunityToolkit.Mvvm (MIT) · CELT 0.7.1 audio codec (BSD) · LAME 3.100 MP3 encoder (LGPL 2) · " +
        "NAudio and Vortice.MediaFoundation on Windows (MIT).";

    private static string Version
    {
        get
        {
            var v = typeof(MainViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
            var plus = v.IndexOf('+');
            return plus > 0 ? v[..plus] : v; // drop the commit hash the SDK appends
        }
    }

    [RelayCommand]
    private void OpenAbout() => IsAboutOpen = true;

    [RelayCommand]
    private void CloseAbout() => IsAboutOpen = false;

    [RelayCommand]
    private void OpenWebsite()
    {
        try
        {
            _host?.OpenUrl(WebsiteUrl);
        }
        catch (Exception ex)
        {
            ShowToast("Cannot open the website", ex.Message, true, null);
        }
    }
}
