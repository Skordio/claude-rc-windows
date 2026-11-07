using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Toolkit.Uwp.Notifications;

namespace ClaudeRc.Tray.Notifications;

/// <summary>
/// Windows toasts through Microsoft.Toolkit.Uwp.Notifications. For an unpackaged exe the toolkit
/// registers the app itself on first use (an AUMID and a COM activator under
/// <c>HKCU\Software\Classes</c>, see docs/m2-decisions.md); <see cref="Unregister"/> removes that again.
/// Created only by the running app, never in tests.
/// </summary>
internal sealed class ToolkitToastNotifier : IToastNotifier, IDisposable
{
    private readonly string? _instance;

    /// <summary>Starts listening for clicks (this registers the app's toast activator).</summary>
    /// <param name="instance">The tray's <c>--instance</c>; a click on another instance's toast is ignored.</param>
    public ToolkitToastNotifier(string? instance)
    {
        _instance = instance;
        ToastNotificationManagerCompat.OnActivated += OnActivated;
    }

    /// <inheritdoc/>
    public event EventHandler<string>? Activated;

    /// <summary>Removes the toolkit's registration and this app's toasts (for the uninstaller, <c>--unregister-notifications</c>).</summary>
    public static void Unregister() => ToastNotificationManagerCompat.Uninstall();

    /// <summary>A short tag per folder, so a newer toast of a folder replaces its older one in the Action Center.</summary>
    public static string TagFor(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())))[..16];

    /// <inheritdoc/>
    public void Show(ToastRequest toast)
    {
        ArgumentNullException.ThrowIfNull(toast);
        try
        {
            var builder = new ToastContentBuilder().AddText(toast.Title).AddText(toast.Text);
            builder.Content.Launch = ToastArgs.Build(toast.Path, _instance);
            builder.Show(t =>
            {
                t.Tag = TagFor(toast.Path);
                t.Group = "folders";
            });
        }
        catch (Exception e) when (e is COMException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            // Notifications turned off for the app in Windows, or no notification platform: nothing to show.
        }
    }

    private void OnActivated(ToastNotificationActivatedEventArgsCompat e)
    {
        if (ToastArgs.TryGetPath(e.Argument, _instance) is { } path)
            Activated?.Invoke(this, path);
    }

    /// <summary>Stops listening. A test instance (<c>--instance</c>) also clears its toasts, so a stale one cannot start the real app.</summary>
    public void Dispose()
    {
        ToastNotificationManagerCompat.OnActivated -= OnActivated;
        if (_instance is not null)
        {
            try
            {
                ToastNotificationManagerCompat.History.Clear();
            }
            catch (Exception e) when (e is COMException or UnauthorizedAccessException or InvalidOperationException)
            {
            }
        }
    }
}
