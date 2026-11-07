using ClaudeRc.Core.Adoption;
using ClaudeRc.Core.Paths;

namespace ClaudeRc.Core.Tests;

public sealed class ExternalSessionMatcherTests
{
    private const string Me = "S-1-5-21-1000-2000-3000-1001";
    private const string Other = "S-1-5-21-1000-2000-3000-1002";
    private const string Cli = @"C:\Users\me\AppData\Roaming\npm\node_modules\@anthropic-ai\claude-code\cli.js";

    // Lexical only, so the tests do not depend on the file system.
    private static string? Lexical(string cwd) => cwd.Length > 3 ? cwd.TrimEnd('\\') : cwd;

    private readonly ExternalSessionMatcher _m = new(Me, [ExternalSessionMatcher.ClaudeImage], matchNodeCli: true, Lexical);

    private static ProcessFacts P(int pid, string image = "claude.exe", string[]? args = null, string? cwd = @"C:\src\app\",
        string? sid = Me, int parent = 1, long? start = null) => new()
        {
            Pid = pid,
            ParentPid = parent,
            ImageName = image,
            Arguments = args ?? ["rc"],
            CurrentDirectory = cwd,
            OwnerSid = sid,
            StartTime = start ?? pid * 10L,
        };

    private IReadOnlyList<ExternalSession> Match(params ProcessFacts[] ps) => _m.Match(ps, new HashSet<int>());

    [Theory]
    [InlineData("claude.exe", new[] { "rc" })]
    [InlineData("CLAUDE.EXE", new[] { "rc" })]
    [InlineData("claude.exe", new[] { "remote-control" })]
    [InlineData("claude.exe", new[] { "rc", "--name", "my session", "--spawn=worktree" })]
    [InlineData("claude.exe", new[] { "remote-control", "--verbose" })]
    [InlineData("node.exe", new[] { Cli, "rc" })]
    [InlineData("node.exe", new[] { "--no-warnings", Cli, "remote-control", "--name", "x" })]
    [InlineData("NODE.EXE", new[] { @"C:\x\node_modules\@anthropic-ai\CLAUDE-CODE\CLI.JS", "rc" })]
    public void Claude_rc_matches(string image, string[] args)
    {
        Assert.True(_m.IsRemoteControl(P(5, image, args)));
        var s = Assert.Single(Match(P(5, image, args)));
        Assert.Equal(new ExternalSession(@"C:\src\app", 5, 50), s);
    }

    [Theory]
    [InlineData("claude.exe", new string[0])]
    [InlineData("claude.exe", new[] { "chat" })]
    [InlineData("claude.exe", new[] { "--name", "rc" })]
    [InlineData("claude.exe", new[] { "RC" })]
    [InlineData("claude.exe", new[] { "rcx" })]
    [InlineData("claude.exe", new[] { "--debug", "rc" })]
    [InlineData("claude.exe", new[] { "-p", "rc" })]
    [InlineData("node.exe", new[] { "rc" })]
    [InlineData("node.exe", new[] { @"C:\other\cli.js", "rc" })]
    [InlineData("node.exe", new[] { Cli })]
    [InlineData("node.exe", new[] { Cli, "chat", "rc" })]
    [InlineData("node.exe", new[] { "-e", "x", Cli, "rc" })]
    [InlineData("claude-code.exe", new[] { "rc" })]
    [InlineData("FakeClaude.exe", new[] { "rc" })]
    [InlineData("cmd.exe", new[] { "/c", "claude", "rc" })]
    public void Other_processes_do_not_match(string image, string[] args)
    {
        Assert.False(_m.IsRemoteControl(P(5, image, args)));
        Assert.Empty(Match(P(5, image, args)));
    }

    [Fact]
    public void Another_users_process_or_an_unknown_owner_does_not_match()
    {
        Assert.Empty(Match(P(5, sid: Other)));
        Assert.Empty(Match(P(6, sid: null)));
        Assert.Single(Match(P(7, sid: Me.ToLowerInvariant())));
    }

    [Fact]
    public void Unknown_arguments_or_directory_do_not_match()
    {
        Assert.Empty(Match(P(5) with { Arguments = null }));
        Assert.Empty(Match(P(6, cwd: null)));
        Assert.Empty(Match(P(7) with { StartTime = null }));
    }

    [Fact]
    public void A_directory_the_normaliser_rejects_does_not_match()
    {
        var m = new ExternalSessionMatcher(Me, ["claude.exe"], true, _ => throw new ArgumentException("bad"));
        Assert.Empty(m.Match([P(5)], new HashSet<int>()));
        var n = new ExternalSessionMatcher(Me, ["claude.exe"], true, _ => null);
        Assert.Empty(n.Match([P(5)], new HashSet<int>()));
    }

    [Fact]
    public void Node_rule_can_be_turned_off()
    {
        var m = new ExternalSessionMatcher(Me, ["claude.exe"], matchNodeCli: false, Lexical);
        Assert.False(m.IsCandidateImage("node.exe"));
        Assert.False(m.IsRemoteControl(P(5, "node.exe", [Cli, "rc"])));
    }

    [Fact]
    public void Candidate_images_are_the_claude_names_and_node()
    {
        Assert.True(_m.IsCandidateImage("claude.exe"));
        Assert.True(_m.IsCandidateImage("Claude.EXE"));
        Assert.True(_m.IsCandidateImage("node.exe"));
        Assert.False(_m.IsCandidateImage("conhost.exe"));
        Assert.False(_m.IsCandidateImage("FakeClaude.exe"));
    }

    [Fact]
    public void For_host_adds_the_file_name_of_an_exe_claude_path_setting()
    {
        var m = ExternalSessionMatcher.ForHost(Me, @"C:\tests\bin\FakeClaude.exe");
        Assert.True(m.IsCandidateImage("FakeClaude.exe"));
        Assert.True(m.IsCandidateImage("claude.exe"));
        Assert.True(m.IsCandidateImage("node.exe"));
        Assert.True(m.IsRemoteControl(P(5, "fakeclaude.exe")));

        // A .cmd shim runs claude.exe (or node): nothing to add. Auto-detect adds nothing either.
        Assert.Equal(["claude.exe"], ExternalSessionMatcher.ForHost(Me, @"C:\npm\claude.cmd").ClaudeImageNames);
        Assert.Equal(["claude.exe"], ExternalSessionMatcher.ForHost(Me, null).ClaudeImageNames);
        // node.exe as the "claude" setting is never a claude image: the node rule decides.
        Assert.Equal(["claude.exe"], ExternalSessionMatcher.ForHost(Me, @"C:\node\node.exe").ClaudeImageNames);
    }

    [Fact]
    public void The_hosts_own_processes_and_their_descendants_are_left_out()
    {
        var hosted = P(10, parent: 2);
        var hostedChild = P(11, parent: 10, args: ["rc"]);
        var external = P(20, parent: 3, cwd: @"C:\src\other");

        var found = _m.Match([hosted, hostedChild, external], new HashSet<int> { 10 });

        Assert.Equal([new ExternalSession(@"C:\src\other", 20, 200)], found);
    }

    [Fact]
    public void An_rc_child_of_an_rc_process_is_counted_once_at_the_top()
    {
        var shell = P(2, "pwsh.exe", args: [], parent: 1);
        var top = P(10, parent: 2);
        var session = P(11, parent: 10, args: ["--session", "x"]);
        var rcChild = P(12, parent: 10, args: ["rc", "--child"]);
        var rcGrandchild = P(13, parent: 11, args: ["remote-control"]);

        var found = _m.Match([shell, top, session, rcChild, rcGrandchild], new HashSet<int>());

        Assert.Equal(10, Assert.Single(found).Pid);
    }

    [Fact]
    public void A_reused_parent_id_is_not_taken_for_the_parent()
    {
        // Pid 10 exited; its id now belongs to a newer rc process. Process 11 (older) is not its child.
        var newer = P(10, start: 500);
        var orphan = P(11, parent: 10, start: 300, cwd: @"C:\src\b");

        var found = _m.Match([newer, orphan], new HashSet<int>());

        Assert.Equal([10, 11], found.Select(s => s.Pid).Order());
    }

    [Fact]
    public void A_reused_parent_id_of_a_hosted_process_does_not_hide_an_external_one()
    {
        var hostedNow = P(10, start: 900);
        var external = P(11, parent: 10, start: 300, cwd: @"C:\src\b");

        var found = _m.Match([hostedNow, external], new HashSet<int> { 10 });

        Assert.Equal(11, Assert.Single(found).Pid);
    }

    [Fact]
    public void Ancestor_walk_stops_at_an_unknown_start_time_and_survives_cycles()
    {
        var unknown = new ProcessFacts { Pid = 2, ParentPid = 3, ImageName = "x.exe" };
        var a = P(10, parent: 2);
        var cycleA = P(20, "x.exe", args: [], parent: 21, start: 1);
        var cycleB = P(21, "x.exe", args: [], parent: 20, start: 1);
        var b = P(22, parent: 20, start: 5, cwd: @"C:\src\b");

        var found = _m.Match([unknown, a, cycleA, cycleB, b], new HashSet<int>());

        Assert.Equal([10, 22], found.Select(s => s.Pid).Order());
    }

    [Fact]
    public void Folder_is_the_normalised_current_directory()
    {
        Assert.Equal(@"C:\src\app", _m.FolderOf(P(5, cwd: @"C:\src\app\")));
        Assert.Null(_m.FolderOf(P(5, cwd: null)));
    }

    [Fact]
    public void Default_normaliser_resolves_case_and_trailing_separator()
    {
        using var dir = new TempDir();
        var folder = dir.CreateDir("MyApp");
        var m = new ExternalSessionMatcher(Me, ["claude.exe"], true);

        var found = m.Match([P(5, cwd: folder.ToLowerInvariant() + "\\")], new HashSet<int>());

        Assert.Equal(PathNormalizer.Normalize(folder), Assert.Single(found).Path);
        Assert.EndsWith("MyApp", found[0].Path, StringComparison.Ordinal);
    }

    [Fact]
    public void By_folder_groups_case_insensitively_oldest_first()
    {
        var groups = ExternalSessionMatcher.ByFolder([
            new ExternalSession(@"C:\src\a", 30, 300),
            new ExternalSession(@"c:\SRC\A", 20, 200),
            new ExternalSession(@"C:\src\b", 40, 400),
        ]);

        Assert.Equal(2, groups.Count);
        Assert.Equal([20, 30], groups[@"C:\SRC\a"].Select(s => s.Pid));
        Assert.Equal([40], groups[@"C:\src\b"].Select(s => s.Pid));
    }

    [Fact]
    public void Still_matches_needs_the_same_process_still_running_claude_rc_in_the_same_folder()
    {
        var expected = new ExternalSession(@"C:\src\app", 5, 50);

        Assert.True(_m.StillMatches(P(5, cwd: @"c:\SRC\app\"), expected));
        Assert.False(_m.StillMatches(P(5, start: 51), expected));          // pid reused
        Assert.False(_m.StillMatches(P(6), expected));                      // another process
        Assert.False(_m.StillMatches(P(5, cwd: @"C:\src\other"), expected)); // moved to another folder
        Assert.False(_m.StillMatches(P(5, args: ["chat"]), expected));
        Assert.False(_m.StillMatches(P(5, sid: Other), expected));
        Assert.False(_m.StillMatches(P(5) with { StartTime = null }, expected));
    }
}
