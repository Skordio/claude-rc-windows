using System.Collections.Frozen;

namespace ClaudeRc.Tray.Viewer;

/// <summary>A file of the viewer page.</summary>
/// <param name="Content">The file's bytes.</param>
/// <param name="ContentType">Its media type.</param>
internal sealed record ViewerFile(byte[] Content, string ContentType);

/// <summary>
/// The viewer page and the vendored xterm.js, embedded in the assembly (<c>viewer/…</c> resources
/// from <c>Viewer/web</c>) and served under <see cref="Origin"/> through WebView2's
/// <c>WebResourceRequested</c>: no files on disk, no network.
/// </summary>
internal static class ViewerContent
{
    /// <summary>The page's origin. <c>.example</c> is reserved (RFC 2606), so it can never be a real site.</summary>
    public const string Origin = "https://viewer.claude-rc.example";

    /// <summary>The page to navigate to.</summary>
    public const string StartUrl = Origin + "/index.html";

    private const string Prefix = "viewer/";

    private static readonly FrozenDictionary<string, string> Types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [""] = "text/plain; charset=utf-8", // LICENSE
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    // Resource names use the folder separator of the build machine; the URLs use '/'.
    private static readonly FrozenDictionary<string, string> Resources = typeof(ViewerContent).Assembly
        .GetManifestResourceNames()
        .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal))
        .ToFrozenDictionary(n => n[Prefix.Length..].Replace('\\', '/'), n => n, StringComparer.Ordinal);

    /// <summary>The file for a request URL, or null when the URL is not one of the page's files.</summary>
    public static ViewerFile? TryGet(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !string.Equals(uri.GetLeftPart(UriPartial.Authority), Origin, StringComparison.OrdinalIgnoreCase))
            return null;
        // AbsolutePath has the dot segments removed and no query or fragment.
        var path = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/');
        if (!Resources.TryGetValue(path, out var name) || !Types.TryGetValue(Path.GetExtension(path), out var type))
            return null;
        using var stream = typeof(ViewerContent).Assembly.GetManifestResourceStream(name);
        if (stream is null)
            return null;
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return new ViewerFile(copy.ToArray(), type);
    }
}
