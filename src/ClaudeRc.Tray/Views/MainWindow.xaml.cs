using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ClaudeRc.Tray.ViewModels;

namespace ClaudeRc.Tray.Views;

/// <summary>The main window (SPEC 3.1). Closing it only hides it; the tray icon stays.</summary>
internal partial class MainWindow : Window
{
    private readonly DispatcherTimer _tick;

    /// <summary>Creates the window for <paramref name="vm"/>.</summary>
    public MainWindow(MainViewModel vm, string? instance)
    {
        InitializeComponent();
        DataContext = vm;
        if (instance is not null)
            Title += $" (instance {instance})";
        _tick = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => vm.Tick(), Dispatcher);
        _tick.Start();
        _unhighlight = new DispatcherTimer(TimeSpan.FromSeconds(4), DispatcherPriority.Background, (_, _) =>
        {
            _unhighlight!.Stop();
            vm.ClearHighlight();
        }, Dispatcher);
        _unhighlight.Stop();
        vm.RowFocusRequested += (_, row) => BringRowIntoView(row);
    }

    private readonly DispatcherTimer _unhighlight;

    // After layout: the window may have been hidden until now, so the row's container may not exist yet.
    private void BringRowIntoView(FolderRowViewModel row)
    {
        _unhighlight.Stop();
        _unhighlight.Start();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (RowsList.ItemContainerGenerator.ContainerFromItem(row) is FrameworkElement container)
                container.BringIntoView();
        });
    }

    /// <summary>Set when the tray app quits, so closing really closes.</summary>
    public bool AllowClose { get; set; }

    /// <summary>Shows the window and brings it to the front.</summary>
    public void ShowAndActivate()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        // Activate alone may only flash the taskbar button when another app has the focus.
        Topmost = true;
        Topmost = false;
        Focus();
    }

    /// <inheritdoc/>
    protected override void OnClosing(CancelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide();
        }
        else
        {
            _tick.Stop();
        }
        base.OnClosing(e);
    }

    private void RowMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.DataContext = button.DataContext;
            menu.PlacementTarget = button;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }
}
