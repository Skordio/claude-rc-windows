using ClaudeRc.Core.Prompts;
using ClaudeRc.Host.Sessions;
using Microsoft.Extensions.Time.Testing;

namespace ClaudeRc.Host.Tests;

public sealed class PromptScannerTests
{
    private sealed class FakeScreen : IScreenSource
    {
        public string[] Lines { get; set; } = [];

        public IReadOnlyList<ScreenRow> GetScreen() => [.. Lines.Select(l => new ScreenRow(l, false))];
    }

    private sealed class Recorder
    {
        public List<string> Events { get; } = [];

        public void Ready(string path) => Events.Add($"ready {path}");

        public void Prompt(string path, string? prompt) => Events.Add($"prompt {path} {prompt ?? "<none>"}");
    }

    private const string Path = @"C:\src\app";
    private static readonly TimeSpan Interval = PromptScanner.DefaultInterval;

    private static (FakeTimeProvider time, Recorder rec, PromptScanner scanner) Create()
    {
        var time = new FakeTimeProvider();
        var rec = new Recorder();
        return (time, rec, new PromptScanner(time, rec.Ready, rec.Prompt));
    }

    [Fact]
    public void Default_interval_is_2_s()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), PromptScanner.DefaultInterval);
    }

    [Fact]
    public void Prompt_is_reported_on_the_next_scan_not_before()
    {
        var (time, rec, scanner) = Create();
        using var _ = scanner;
        var screen = new FakeScreen { Lines = ["Trust C:\\src\\app? [y/N]"] };
        scanner.Add(Path, screen);

        time.Advance(Interval - TimeSpan.FromMilliseconds(1));
        Assert.Empty(rec.Events);
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal([$"prompt {Path} Trust C:\\src\\app? [y/N]"], rec.Events);
    }

    [Fact]
    public void Same_prompt_is_not_reported_twice_and_an_answer_clears_it()
    {
        var (time, rec, scanner) = Create();
        using var _ = scanner;
        var screen = new FakeScreen { Lines = ["Choose [1/2] (default: 1):"] };
        scanner.Add(Path, screen);

        time.Advance(Interval);
        time.Advance(Interval);
        screen.Lines = ["Choose [1/2] (default: 1): 1"];
        time.Advance(Interval);
        time.Advance(Interval);
        Assert.Equal([$"prompt {Path} Choose [1/2] (default: 1):", $"prompt {Path} <none>"], rec.Events);
    }

    [Fact]
    public void No_prompt_at_start_reports_nothing()
    {
        var (time, rec, scanner) = Create();
        using var _ = scanner;
        scanner.Add(Path, new FakeScreen { Lines = ["starting…"] });
        time.Advance(Interval * 3);
        Assert.Empty(rec.Events);
    }

    [Fact]
    public void Ready_is_reported_once()
    {
        var (time, rec, scanner) = Create();
        using var _ = scanner;
        var screen = new FakeScreen { Lines = ["Connecting…"] };
        scanner.Add(Path, screen);
        time.Advance(Interval);
        screen.Lines = ["Continue coding in the Claude mobile app or https://claude.ai/code", "space to show QR code"];
        time.Advance(Interval);
        time.Advance(Interval);
        Assert.Equal([$"ready {Path}"], rec.Events);
    }

    [Fact]
    public void Removed_sessions_are_no_longer_scanned_and_paths_compare_case_insensitively()
    {
        var (time, rec, scanner) = Create();
        using var _ = scanner;
        scanner.Add(Path, new FakeScreen { Lines = ["Proceed (y/n)"] });
        scanner.Remove(Path.ToUpperInvariant());
        time.Advance(Interval * 2);
        Assert.Empty(rec.Events);
    }

    [Fact]
    public void Adding_again_replaces_the_screen_and_starts_fresh()
    {
        var (time, rec, scanner) = Create();
        using var _ = scanner;
        scanner.Add(Path, new FakeScreen { Lines = ["Proceed (y/n)"] });
        time.Advance(Interval);
        scanner.Add(Path, new FakeScreen { Lines = ["Proceed (y/n)"] });
        time.Advance(Interval);
        Assert.Equal([$"prompt {Path} Proceed (y/n)", $"prompt {Path} Proceed (y/n)"], rec.Events);
    }

    [Fact]
    public void Several_sessions_are_scanned_independently()
    {
        var (time, rec, scanner) = Create();
        using var _ = scanner;
        scanner.Add(@"C:\a", new FakeScreen { Lines = ["Continue? [Y/n]"] });
        scanner.Add(@"C:\b", new FakeScreen { Lines = ["fine"] });
        time.Advance(Interval);
        Assert.Equal([@"prompt C:\a Continue? [Y/n]"], rec.Events);
    }

    [Fact]
    public void Dispose_stops_scanning()
    {
        var (time, rec, scanner) = Create();
        scanner.Add(Path, new FakeScreen { Lines = ["Proceed (y/n)"] });
        scanner.Dispose();
        time.Advance(Interval * 2);
        Assert.Empty(rec.Events);
    }

    [Fact]
    public void A_failing_screen_does_not_stop_the_others()
    {
        var (time, rec, scanner) = Create();
        using var _ = scanner;
        scanner.Add(@"C:\bad", new ThrowingScreen());
        scanner.Add(@"C:\good", new FakeScreen { Lines = ["Proceed (y/n)"] });
        time.Advance(Interval);
        Assert.Equal([@"prompt C:\good Proceed (y/n)"], rec.Events);
    }

    private sealed class ThrowingScreen : IScreenSource
    {
        public IReadOnlyList<ScreenRow> GetScreen() => throw new InvalidOperationException("boom");
    }
}
