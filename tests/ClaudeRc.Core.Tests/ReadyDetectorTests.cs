using ClaudeRc.Core.Prompts;

namespace ClaudeRc.Core.Tests;

public sealed class ReadyDetectorTests
{
    private static ScreenRow[] Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name))
            .Split('\n').Select(r => new ScreenRow(r, r.Length >= 120)).ToArray();

    [Theory]
    [InlineData("claude-rc-ready.txt")]
    [InlineData("claude-rc-ready-nongit.txt")]
    [InlineData("claude-rc-ready-after-prompts.txt")]
    public void Ready_screens_are_ready(string fixture)
    {
        Assert.True(ReadyDetector.IsReady(Fixture(fixture)));
    }

    [Theory]
    [InlineData("prompt-trust.txt")]
    [InlineData("prompt-spawn-mode.txt")]
    public void Prompt_screens_are_not_ready(string fixture)
    {
        Assert.False(ReadyDetector.IsReady(Fixture(fixture)));
    }

    [Fact]
    public void Marker_split_by_a_soft_wrap_is_found()
    {
        ScreenRow[] rows = [new("x".PadRight(100) + "Continue coding in the Cla", true), new("ude mobile app or https://…", false)];
        Assert.True(ReadyDetector.IsReady(rows));
    }

    [Fact]
    public void Raw_output_chunks_can_be_checked_too()
    {
        Assert.True(ReadyDetector.ContainsMarker("\u001b[24;1HContinue coding in the Claude mobile app or \u001b]8;;https://x\u001b\\"));
        Assert.False(ReadyDetector.ContainsMarker("Connected"));
    }
}
