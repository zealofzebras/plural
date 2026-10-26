using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
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
