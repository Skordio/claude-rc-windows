using System.Windows;
using ClaudeRc.Tray.ViewModels;

namespace ClaudeRc.Tray.Views;

/// <summary>The settings window (SPEC 3.4).</summary>
internal partial class SettingsWindow : Window
{
    /// <summary>Creates the window; it closes itself after a successful save.</summary>
    public SettingsWindow(SettingsViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);
        InitializeComponent();
        DataContext = vm;
        vm.CloseRequested += (_, _) => Close();
        if (Application.Current?.MainWindow is { IsVisible: true } main && !ReferenceEquals(main, this))
            Owner = main;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
