using System.Windows;
using System.Windows.Controls;
using ClaudeRc.Tray.ViewModels;

namespace ClaudeRc.Tray.Views;

/// <summary>The folder log viewer (row menu → View log).</summary>
internal partial class LogWindow : Window
{
    /// <summary>Creates the window.</summary>
    public LogWindow(LogViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        if (Application.Current?.MainWindow is { IsVisible: true } main && !ReferenceEquals(main, this))
            Owner = main;
    }

    // The newest lines are at the end.
    private void LogText_TextChanged(object sender, TextChangedEventArgs e) => LogText.ScrollToEnd();
}
