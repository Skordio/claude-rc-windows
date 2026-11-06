using ClaudeRc.Core;
using ClaudeRc.Core.Ipc;
using ClaudeRc.Core.Paths;
using ClaudeRc.Tray.Hosting;
using Microsoft.Toolkit.Uwp.Notifications;

namespace ClaudeRc.Tray.Notifications;

/// <summary>A toast to show.</summary>
/// <param name="Path">The folder (the click opens its row).</param>
/// <param name="Title">First line.</param>
/// <param name="Text">Second line: the host's text.</param>
internal sealed record ToastRequest(string Path, string Title, string Text);

/// <summary>Shows Windows toasts; a fake in tests.</summary>
internal interface IToastNotifier
{
    /// <summary>Shows a toast.</summary>
    void Show(ToastRequest toast);

    /// <summary>A toast of this app was clicked; the argument is its folder. May be raised on any thread.</summary>
    event EventHandler<string>? Activated;
}

/// <summary>The arguments a toast carries back when clicked.</summary>
internal static class ToastArgs
{
    private const string Action = "action";
    private const string Open = "open";
    private const string PathKey = "path";
    private const string InstanceKey = "instance";

    /// <summary>Encodes the folder and the <c>--instance</c> that showed the toast.</summary>
    public static string Build(string path, string? instance) =>
        new ToastArguments().Add(Action, Open).Add(PathKey, path).Add(InstanceKey, instance ?? "").ToString();

    /// <summary>
    /// The folder of a clicked toast, or null when the arguments are not an "open" toast of this
    /// instance (a test instance never acts on the real app's toasts, nor the other way round).
    /// </summary>
    public static string? TryGetPath(string arguments, string? instance)
    {
        try
        {
            var args = ToastArguments.Parse(arguments ?? "");
            if (!args.TryGetValue(Action, out string? action) || action != Open)
                return null;
            args.TryGetValue(InstanceKey, out string? shownBy);
            if (!string.Equals(shownBy ?? "", instance ?? "", StringComparison.Ordinal))
                return null;
            return args.TryGetValue(PathKey, out string? path) && !string.IsNullOrEmpty(path) ? path : null;
        }
        catch (Exception e) when (e is ArgumentException or FormatException or InvalidOperationException or KeyNotFoundException)
        {
            return null;
        }
    }
}

/// <summary>
/// Turns the host's <c>toast</c> events into Windows toasts (SPEC 3.5). The host sends one per
/// transition into <c>failing</c>, <c>waiting</c> or <c>missing</c>, and only while notifications
/// are on. The tray checks both again: it skips a toast while the setting it read is off, and shows
/// a status only once until the folder's row has left it. A click opens the main window at the row.
/// </summary>
internal sealed class ToastRouter : IDisposable
{
    private readonly HostConnection _connection;
    private readonly IToastNotifier _notifier;
    private readonly Action<string> _openRow;
    private readonly Dictionary<string, FolderStatus> _shown = new(PathNormalizer.Comparer);
    private readonly Lock _lock = new();
    private bool _disposed;

    /// <summary>Follows <paramref name="connection"/>'s toast events.</summary>
    /// <param name="connection">The host connection.</param>
    /// <param name="notifier">Shows the toasts.</param>
    /// <param name="openRow">Opens the main window at a folder's row; called on the notifier's thread.</param>
    public ToastRouter(HostConnection connection, IToastNotifier notifier, Action<string> openRow)
    {
        _connection = connection;
        _notifier = notifier;
        _openRow = openRow;
        _connection.Toast += OnToast;
        _connection.RowsChanged += OnRowsChanged;
        _notifier.Activated += OnActivated;
    }

    /// <summary>The first line for a status.</summary>
    public static string TitleFor(FolderStatus status) => status switch
    {
        FolderStatus.Failing => "claude rc is failing",
        FolderStatus.Waiting => "Waiting for input",
        FolderStatus.Missing => "Folder missing",
        _ => "claude-rc-windows",
    };

    private void OnToast(object? sender, ToastEvent e)
    {
        if (_connection.Settings is { Notifications: false })
            return;
        lock (_lock)
        {
            if (_disposed || (_shown.TryGetValue(e.Path, out var shown) && shown == e.Status))
                return;
            _shown[e.Path] = e.Status;
        }
        _notifier.Show(new ToastRequest(e.Path, TitleFor(e.Status), e.Text));
    }

    // A folder that has left the status it was toasted for may be toasted for it again.
    private void OnRowsChanged(object? sender, EventArgs e)
    {
        var rows = _connection.Rows.ToDictionary(r => r.Path, r => r.Status, PathNormalizer.Comparer);
        lock (_lock)
        {
            foreach (var (path, status) in _shown.ToList())
            {
                if (!rows.TryGetValue(path, out var now) || now != status)
                    _shown.Remove(path);
            }
        }
    }

    private void OnActivated(object? sender, string path)
    {
        lock (_lock)
        {
            if (_disposed)
                return;
        }
        _openRow(path);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_lock)
            _disposed = true;
        _connection.Toast -= OnToast;
        _connection.RowsChanged -= OnRowsChanged;
        _notifier.Activated -= OnActivated;
    }
}
