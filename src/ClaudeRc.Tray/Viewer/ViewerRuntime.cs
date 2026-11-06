using Microsoft.Web.WebView2.Core;

namespace ClaudeRc.Tray.Viewer;

/// <summary>What the console window says when WebView2 cannot start.</summary>
internal static class ViewerRuntime
{
    /// <summary>Microsoft's download page for the Evergreen WebView2 Runtime.</summary>
    public const string DownloadUrl = "https://developer.microsoft.com/microsoft-edge/webview2/";

    /// <summary>The message for an exception from creating or starting WebView2.</summary>
    public static string Describe(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error is WebView2RuntimeNotFoundException
            ? "The console viewer needs the Microsoft Edge WebView2 Runtime, which is not installed on this computer.\n\n"
              + $"Install the Evergreen WebView2 Runtime from {DownloadUrl}, then open the console again. "
              + "The folder's claude rc session is not affected."
            : $"The console viewer could not start: {error.Message}\n\nThe folder's claude rc session is not affected.";
    }
}
