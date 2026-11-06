using System.Runtime.InteropServices;
using System.Windows;
using ClaudeRc.Tray.Viewer;
using ClaudeRc.Tray.ViewModels;
using Microsoft.Web.WebView2.Core;

namespace ClaudeRc.Tray.Views;

/// <summary>
/// The console viewer (SPEC 3.3): WebView2 showing the embedded xterm.js page. Only the page's own
/// files are served (<see cref="ViewerContent"/>); every other request and navigation is refused,
/// so the page never reaches the network. Closing the window detaches; the session is not touched.
/// </summary>
internal partial class ConsoleWindow : Window
{
    private readonly ConsoleViewModel _vm;
    private readonly Func<Task<CoreWebView2Environment>> _environment;
    private bool _closed;

    /// <summary>Creates the window.</summary>
    /// <param name="vm">The folder's viewer.</param>
    /// <param name="environment">The shared WebView2 environment (user data folder, SPEC: instance-suffixed).</param>
    public ConsoleWindow(ConsoleViewModel vm, Func<Task<CoreWebView2Environment>> environment)
    {
        InitializeComponent();
        DataContext = vm;
        _vm = vm;
        _environment = environment;
        View.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 0x1E, 0x1E, 0x1E);
        Loaded += OnLoaded;
        Activated += (_, _) => _vm.Focus();
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var environment = await _environment();
            if (_closed)
                return;
            await View.EnsureCoreWebView2Async(environment);
        }
        catch (ObjectDisposedException) when (_closed)
        {
            return; // closed while WebView2 was starting
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or COMException or UnauthorizedAccessException
                                       or IOException or InvalidOperationException or ArgumentException)
        {
            if (!_closed)
                ShowMessage(ViewerRuntime.Describe(ex));
            return;
        }
        if (_closed)
            return;

        var core = View.CoreWebView2;
        var settings = core.Settings;
        settings.AreDevToolsEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreDefaultScriptDialogsEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false; // F5, Ctrl+F, Ctrl+P, … reach the terminal instead
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.IsPinchZoomEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsBuiltInErrorPageEnabled = false;
        settings.IsWebMessageEnabled = true;

        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnWebResourceRequested;
        core.NavigationStarting += (_, args) =>
        {
            if (!args.Uri.StartsWith(ViewerContent.Origin + "/", StringComparison.OrdinalIgnoreCase))
                args.Cancel = true;
        };
        core.NewWindowRequested += (_, args) => args.Handled = true;
        core.ProcessFailed += (_, args) => ShowMessage(
            $"The console viewer stopped ({args.ProcessFailedKind}). Close this window and open the console again.\n\n"
            + "The folder's claude rc session is not affected.");
        core.WebMessageReceived += (_, args) =>
        {
            if (args.Source.StartsWith(ViewerContent.Origin + "/", StringComparison.OrdinalIgnoreCase))
                _vm.OnPageMessage(args.WebMessageAsJson);
        };
        _vm.ConnectPage(new WebViewPage(core));
        core.Navigate(ViewerContent.StartUrl);
        View.Focus();
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var environment = View.CoreWebView2.Environment;
        e.Response = ViewerContent.TryGet(e.Request.Uri) is { } file
            ? environment.CreateWebResourceResponse(new MemoryStream(file.Content), 200, "OK",
                $"Content-Type: {file.ContentType}\r\nCache-Control: no-store")
            : environment.CreateWebResourceResponse(null, 404, "Not Found", "");
    }

    private void ShowMessage(string text)
    {
        MessageText.Text = text;
        MessagePanel.Visibility = Visibility.Visible;
        View.Visibility = Visibility.Collapsed;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _vm.Dispose();
        View.Dispose();
    }

    /// <summary>Posts to the page; after the page is gone there is nothing to post to.</summary>
    private sealed class WebViewPage(CoreWebView2 core) : IViewerPage
    {
        public void Post(string json)
        {
            try
            {
                core.PostWebMessageAsJson(json);
            }
            catch (Exception e) when (e is InvalidOperationException or COMException or ObjectDisposedException)
            {
            }
        }
    }
}
