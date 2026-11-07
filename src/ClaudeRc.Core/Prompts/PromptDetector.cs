using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeRc.Core.Prompts;

/// <summary>One row of the emulated screen.</summary>
/// <param name="Text">The row's characters (trailing blanks may or may not be included).</param>
/// <param name="Wrapped">
/// True when the row was soft-wrapped: the line continues on the next row because it reached
/// the right margin, not because of a line break.
/// </param>
public readonly record struct ScreenRow(string Text, bool Wrapped);

/// <summary>
/// The waiting-for-input heuristic (SPEC 8). Looks only at the last non-empty logical line of
/// the screen (soft-wrapped rows joined). It is a prompt when it contains a bracketed choice
/// (<c>[1/2]</c>, <c>[1/2/3]</c>, <c>[y/N]</c>, <c>[Y/n]</c>, <c>[yes/no]</c>, <c>(y/n)</c>,
/// <c>(yes/no)</c>, case-insensitive) and ends with <c>:</c>, <c>?</c>, <c>]</c> or <c>)</c>,
/// meaning nothing has been typed after it.
/// </summary>
public static partial class PromptDetector
{
    /// <summary>Longest prompt text returned; longer lines keep their start and end around an ellipsis.</summary>
    public const int MaxPromptLength = 200;

    [GeneratedRegex(@"\[(?:\d+(?:/\d+)+|y/n|yes/no)\]|\((?:y/n|yes/no)\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Choice();

    /// <summary>
    /// Returns the prompt text (trimmed, at most <see cref="MaxPromptLength"/> characters) when
    /// the screen ends at an unanswered prompt, otherwise <see langword="null"/>.
    /// </summary>
    /// <param name="rows">The screen, top to bottom.</param>
    public static string? Detect(IReadOnlyList<ScreenRow> rows)
    {
        var line = LastLogicalLine(rows);
        if (line is null || !IsPrompt(line))
            return null;
        return Shorten(line);
    }

    /// <summary>
    /// The last non-empty line of the screen with the rows that soft-wrap into it joined, trimmed;
    /// <see langword="null"/> for an empty screen.
    /// </summary>
    /// <param name="rows">The screen, top to bottom.</param>
    public static string? LastLogicalLine(IReadOnlyList<ScreenRow> rows)
    {
        var last = rows.Count - 1;
        while (last >= 0 && string.IsNullOrWhiteSpace(rows[last].Text))
            last--;
        if (last < 0)
            return null;
        var first = last;
        while (first > 0 && rows[first - 1].Wrapped)
            first--;
        var sb = new StringBuilder();
        for (var i = first; i <= last; i++)
            sb.Append(rows[i].Text);
        return sb.ToString().Trim();
    }

    /// <summary>Whether a single logical line looks like an unanswered prompt.</summary>
    public static bool IsPrompt(string line)
    {
        var trimmed = line.TrimEnd();
        if (trimmed.Length == 0 || trimmed[^1] is not (':' or '?' or ']' or ')'))
            return false;
        return Choice().IsMatch(trimmed);
    }

    private static string Shorten(string line)
    {
        if (line.Length <= MaxPromptLength)
            return line;
        const string Ellipsis = "…";
        var head = (MaxPromptLength - Ellipsis.Length) / 2;
        var tail = MaxPromptLength - Ellipsis.Length - head;
        return string.Concat(line.AsSpan(0, head), Ellipsis, line.AsSpan(line.Length - tail));
    }
}
