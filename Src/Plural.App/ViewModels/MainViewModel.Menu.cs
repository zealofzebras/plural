using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Plural.App.ViewModels;

/// <summary>The header menu, shown as a sheet like Takes (full screen on phones).</summary>
public partial class MainViewModel
{
    [ObservableProperty] public partial bool IsMenuOpen { get; set; }

    [RelayCommand]
    private void OpenMenu() => IsMenuOpen = true;

    [RelayCommand]
    private void CloseMenu() => IsMenuOpen = false;

    /// <summary>Closes the menu, then runs the chosen item's command (which may open another sheet).</summary>
    [RelayCommand]
    private void RunMenuItem(ICommand? command)
    {
        IsMenuOpen = false;
        if (command?.CanExecute(null) == true)
        {
            command.Execute(null);
        }
    }
}
