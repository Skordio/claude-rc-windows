using System.Text;

namespace ClaudeRc.Core.Prompts;

/// <summary>
/// Recognises claude rc's ready output, the <c>starting</c> → <c>running</c> marker of
/// m0-findings 2: the line <c>Continue coding in the Claude mobile app or …</c>, which appears
/// about 2 s after start. Feed the result to <c>Supervisor.ReadySeen</c>.
/// </summary>
public static class ReadyDetector
{
    /// <summary>The text claude rc prints once it is connected.</summary>
    public const string ReadyMarker = "Continue coding in the Claude mobile app";

    /// <summary>True when any logical line of the screen (soft-wrapped rows joined) contains <see cref="ReadyMarker"/>.</summary>
    public static bool IsReady(IReadOnlyList<ScreenRow> rows)
    {
        var line = new StringBuilder();
        foreach (var row in rows)
        {
            line.Append(row.Text);
            if (row.Wrapped)
                continue;
            if (line.ToString().Contains(ReadyMarker, StringComparison.Ordinal))
                return true;
            line.Clear();
        }
        return line.ToString().Contains(ReadyMarker, StringComparison.Ordinal);
    }

    /// <summary>
    /// True when a decoded output chunk contains <see cref="ReadyMarker"/> verbatim. Cheaper than a
    /// screen check, but a marker split across chunks or cursor moves is missed; use
    /// <see cref="IsReady"/> as the reliable check.
    /// </summary>
    public static bool ContainsMarker(string output) => output.Contains(ReadyMarker, StringComparison.Ordinal);
}
