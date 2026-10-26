using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Plural.App.Services;
using Plural.App.ViewModels;
using Plural.App.Views;

namespace Plural.App;

public partial class App : Application
{
    /// <summary>Set by the platform head before Avalonia starts.</summary>
    public static IAppHost? Host { get; set; }

    /// <summary>One view model per process; Android may recreate the view when the activity is recreated.</summary>
    public static MainViewModel? MainViewModel { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        try
        {
            if (Host?.WindowBackground is { } bg)
            {
                UseSystemBackground(ThemeVariant.Light, bg.Light);
                UseSystemBackground(ThemeVariant.Dark, bg.Dark);
            }
        }
        catch (Exception)
        {
            // Cosmetic only: keep Plural's own palette if the system colors cannot be read.
        }
    }

    /// <summary>
    /// Replaces the neutral surfaces of one theme with the system window background, so the content blends with the native title bar.
    /// Cards and tracks step away from it (lighter in dark mode, darker in light) to stay visible on it.
    /// </summary>
    private void UseSystemBackground(ThemeVariant variant, uint rgb)
    {
        var bg = Color.FromUInt32(0xFF000000 | rgb);
        var dark = variant == ThemeVariant.Dark;
        Color Step(double amount)
        {
            var target = dark ? 255 : 0;
            byte Mix(byte c) => (byte)Math.Round(c + (target - c) * amount);
            return Color.FromRgb(Mix(bg.R), Mix(bg.G), Mix(bg.B));
        }

        var theme = (ResourceDictionary)Resources.ThemeDictionaries[variant];
        theme["BgBrush"] = new SolidColorBrush(bg);
        theme["SurfaceBrush"] = new SolidColorBrush(Step(dark ? 0.045 : 0.035));
        theme["Surface2Brush"] = new SolidColorBrush(Step(dark ? 0.09 : 0.07));
        theme["StrokeBrush"] = new SolidColorBrush(Step(dark ? 0.12 : 0.11));
        theme["MeterTrackBrush"] = new SolidColorBrush(Step(dark ? 0.10 : 0.09));
        foreach (var fluent in Styles.OfType<FluentTheme>())
        {
            if (fluent.Palettes.TryGetValue(variant, out var palette))
            {
                palette.RegionColor = bg;
            }
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var host = Host ?? throw new InvalidOperationException("App.Host must be set by the platform head.");
        var vm = MainViewModel ??= new MainViewModel(host);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow { DataContext = vm };
            desktop.ShutdownRequested += (_, _) => vm.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        else if (ApplicationLifetime is IActivityApplicationLifetime activity)
        {
            activity.MainViewFactory = () => new MainView { DataContext = vm };
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime single)
        {
            single.MainView = new MainView { DataContext = vm };
        }

        base.OnFrameworkInitializationCompleted();
    }

    // macOS application menu (App.axaml); replaces Avalonia's default "About Avalonia".
    private void OnAboutMenuClick(object? sender, EventArgs e) => MainViewModel?.OpenAboutCommand.Execute(null);
}
