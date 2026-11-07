using System.Text;

namespace ClaudeRc.Core.Processes;

/// <summary>
/// Builds a Windows command line from an argument list with the quoting rules that
/// <c>CommandLineToArgvW</c> and the MSVC runtime parse back (the algorithm of .NET's
/// <c>PasteArguments</c>, i.e. <c>ProcessStartInfo.ArgumentList</c> semantics, SPEC 6.3). No
/// shell is involved, so <c>&amp;</c>, <c>%</c> and the like reach the program unchanged.
/// </summary>
public static class CommandLine
{
    /// <summary>The program followed by each argument, quoted where needed.</summary>
    public static string Build(string program, IEnumerable<string> arguments)
    {
        var sb = new StringBuilder();
        // argv[0] is parsed without escape processing: quote it only when it has blanks.
        if (program.AsSpan().IndexOfAny(' ', '\t') >= 0)
            sb.Append('"').Append(program).Append('"');
        else
            sb.Append(program);
        foreach (var argument in arguments)
            AppendArgument(sb.Append(' '), argument);
        return sb.ToString();
    }

    /// <summary>
    /// Only the arguments, each quoted where needed and separated by spaces: what goes after the
    /// program (a scheduled task's <c>Arguments</c>, <c>ShellExecute</c>'s parameters).
    /// </summary>
    public static string JoinArguments(IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var sb = new StringBuilder();
        var first = true;
        foreach (var argument in arguments)
        {
            if (!first)
                sb.Append(' ');
            first = false;
            AppendArgument(sb, argument);
        }
        return sb.ToString();
    }

    private static void AppendArgument(StringBuilder sb, string arg)
    {
        if (arg.Length != 0 && arg.AsSpan().IndexOfAny(' ', '\t', '"') < 0)
        {
            sb.Append(arg);
            return;
        }
        sb.Append('"');
        for (var i = 0; i < arg.Length;)
        {
            var c = arg[i++];
            if (c == '\\')
            {
                var backslashes = 1;
                while (i < arg.Length && arg[i] == '\\')
                {
                    i++;
                    backslashes++;
                }
                if (i == arg.Length)
                    sb.Append('\\', backslashes * 2); // before the closing quote
                else if (arg[i] == '"')
                {
                    sb.Append('\\', backslashes * 2 + 1).Append('"');
                    i++;
                }
                else
                    sb.Append('\\', backslashes);
            }
            else if (c == '"')
                sb.Append('\\').Append('"');
            else
                sb.Append(c);
        }
        sb.Append('"');
    }
}
