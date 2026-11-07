using ClaudeRc.Core.Prompts;

namespace ClaudeRc.Core.Tests;

public sealed class PromptDetectorTests
{
    private const int FixtureColumns = 120;

    // The .txt fixtures are 120-column screens rendered with pyte; a row that fills all 120
    // columns is a soft wrap (m0-findings 2).
    private static ScreenRow[] Fixture(string name)
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name));
        return text.Replace("\r\n", "\n").Split('\n')
            .Select(r => new ScreenRow(r, r.Length >= FixtureColumns))
            .ToArray();
    }

    private static ScreenRow[] Rows(params string[] rows) => rows.Select(r => new ScreenRow(r, false)).ToArray();

    [Theory]
    [InlineData("claude-rc-ready.txt")]
    [InlineData("claude-rc-ready-nongit.txt")]
    [InlineData("claude-rc-ready-after-prompts.txt")]
    [InlineData("claude-rc-stopping.txt")]
    public void Ready_and_stopping_screens_are_not_prompts(string fixture)
    {
        Assert.Null(PromptDetector.Detect(Fixture(fixture)));
    }

    [Fact]
    public void Trust_prompt_is_detected_with_its_wrapped_path_joined()
    {
        var prompt = PromptDetector.Detect(Fixture("prompt-trust.txt"));
        Assert.NotNull(prompt);
        Assert.StartsWith(@"Trust C:\Users\Steven\", prompt);
        Assert.EndsWith(@"\m0\runs\gitA? [y/N]", prompt);
        Assert.DoesNotContain("\n", prompt);
    }

    [Fact]
    public void Spawn_mode_prompt_is_detected()
    {
        Assert.Equal("Choose [1/2] (default: 1):", PromptDetector.Detect(Fixture("prompt-spawn-mode.txt")));
    }

    [Theory]
    [InlineData("Choose [1/2] (default: 1):")]
    [InlineData(@"Trust C:\Users\me\src\app? [y/N]")]
    [InlineData("Continue? [Y/n]")]
    [InlineData("Overwrite? [Y/N]")]
    [InlineData("Proceed [yes/no]?")]
    [InlineData("Proceed (y/n)")]
    [InlineData("Proceed (yes/no):")]
    [InlineData("Pick one [1/2/3]:")]
    [InlineData("Continue? [y/N]    ")]
    public void Bracketed_choice_at_the_end_is_a_prompt(string line)
    {
        Assert.Equal(line.Trim(), PromptDetector.Detect(Rows("some output", line, "", "  ")));
    }

    [Theory]
    [InlineData("Continue coding in the Claude mobile app or https://claude.ai/code?environment=env_01XXXXXXXXXXXXXXXXXXXXXX")]
    [InlineData("Continue coding in the Claude mobile app or …")]
    [InlineData("    Capacity: 0/32 · New sessions will be created in the current directory")]
    [InlineData("space to show QR code")]
    [InlineData("space to show QR code · w to toggle spawn mode")]
    [InlineData("Spawn mode: same-dir (saved for this folder; press w to change)")]
    [InlineData("[10:11:33] Shutting down 1 active session(s)…")]
    [InlineData("[10:13:14] Environment preserved. Restart `claude remote-control` to reconnect existing sessions.")]
    [InlineData(@"Trust C:\Users\me\src\app? [y/N] y")]
    [InlineData("Choose [1/2] (default: 1): 2")]
    [InlineData("Is this a question?")]
    [InlineData("Array index [1]:")]
    [InlineData("·✔︎· Connected · gitB · master")]
    public void Normal_output_is_not_a_prompt(string line)
    {
        Assert.Null(PromptDetector.Detect(Rows("Choose [1/2] (default: 1):", line)));
    }

    [Fact]
    public void Choice_split_across_a_soft_wrap_is_joined()
    {
        var rows = new[]
        {
            new ScreenRow("earlier", false),
            new ScreenRow(@"Trust C:\very\long\path [y/", true),
            new ScreenRow("N]", false),
        };
        Assert.Equal(@"Trust C:\very\long\path [y/N]", PromptDetector.Detect(rows));
    }

    [Fact]
    public void Rows_without_a_soft_wrap_are_not_joined()
    {
        var rows = new[]
        {
            new ScreenRow("Continue? [y/", false),
            new ScreenRow("N]", false),
        };
        Assert.Null(PromptDetector.Detect(rows));
    }

    [Fact]
    public void Several_wrapped_rows_are_joined_in_order()
    {
        var rows = new[]
        {
            new ScreenRow("not part", false),
            new ScreenRow("Trust ", true),
            new ScreenRow(@"C:\a\", true),
            new ScreenRow("b? [y/N]", false),
        };
        Assert.Equal(@"Trust C:\a\b? [y/N]", PromptDetector.LastLogicalLine(rows));
        Assert.Equal(@"Trust C:\a\b? [y/N]", PromptDetector.Detect(rows));
    }

    [Fact]
    public void Empty_screen_has_no_prompt()
    {
        Assert.Null(PromptDetector.Detect([]));
        Assert.Null(PromptDetector.Detect(Rows("", "   ", "")));
    }

    [Fact]
    public void Long_prompt_is_cut_to_200_characters_keeping_both_ends()
    {
        var line = "Trust C:\\" + new string('x', 300) + "? [y/N]";
        var prompt = PromptDetector.Detect(Rows(line));
        Assert.NotNull(prompt);
        Assert.Equal(PromptDetector.MaxPromptLength, prompt.Length);
        Assert.StartsWith("Trust C:\\xxx", prompt);
        Assert.EndsWith("xxx? [y/N]", prompt);
        Assert.Contains("…", prompt);
    }
}
