namespace ClaudeRc.Core.Paths;

/// <summary>
/// Display names for folder rows (SPEC 3.1): the basename, or <c>parent\name</c> when two
/// folders share a basename (compared case-insensitively). If <c>parent\name</c> still
/// collides, more leading segments are added until the names differ.
/// </summary>
public static class DisplayNames
{
    /// <summary>
    /// Computes the display name of every path. The result is keyed by path with
    /// <see cref="PathNormalizer.Comparer"/>.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Compute(IEnumerable<string> normalizedPaths)
    {
        var entries = normalizedPaths
            .Distinct(PathNormalizer.Comparer)
            .Select(p => new Entry(p, Segments(p)))
            .ToList();

        // Grow the suffix of every colliding entry until no collisions are left.
        while (true)
        {
            var collisions = entries
                .GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .SelectMany(g => g)
                .Where(e => e.Depth < e.Segments.Length)
                .ToList();
            if (collisions.Count == 0)
                break;
            foreach (var e in collisions)
                e.Depth++;
        }

        var result = new Dictionary<string, string>(PathNormalizer.Comparer);
        foreach (var e in entries)
            result[e.Path] = e.Name;
        return result;
    }

    private static string[] Segments(string path)
    {
        var root = Path.GetPathRoot(path) ?? "";
        var rest = path[root.Length..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (rest.Length == 0)
            return [path]; // a root names itself: "C:\"
        var rootParts = root.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        return [.. rootParts, .. rest];
    }

    private sealed class Entry(string path, string[] segments)
    {
        public string Path { get; } = path;
        public string[] Segments { get; } = segments;
        public int Depth { get; set; } = 1;
        public string Name => string.Join('\\', Segments[^Depth..]);
    }
}
