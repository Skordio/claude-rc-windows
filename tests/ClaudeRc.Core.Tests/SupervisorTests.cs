using ClaudeRc.Core.Storage;
using ClaudeRc.Core.Supervision;
using Microsoft.Extensions.Time.Testing;

namespace ClaudeRc.Core.Tests;

public sealed class SupervisorTests
{
    private const string A = @"C:\src\a";
    private const string B = @"C:\src\b";
    private const string C = @"D:\work\c";

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 6, 8, 0, 0, TimeSpan.Zero));
    private readonly HashSet<string> _existing = new(StringComparer.OrdinalIgnoreCase) { A, B, C };
    private readonly List<(string Path, TimeSpan At)> _existsChecks = [];
    private readonly DateTimeOffset _t0;
    private readonly Supervisor _sup;
    private int _nextPid = 100;

    public SupervisorTests()
    {
        _t0 = _time.GetUtcNow();
        _sup = new Supervisor(_time, p =>
        {
            _existsChecks.Add((p, _time.GetUtcNow() - _t0));
            return _existing.Contains(p);
        });
    }

    // ---- helpers -------------------------------------------------------------------------

    private FolderStatus Status(string path) => _sup.Get(path)!.Status;

    private static bool Starts(SupervisorResult r, string path) => r.Effects.OfType<StartSession>().Any(s => s.Path == path);
    private static bool Stops(SupervisorResult r, string path) => r.Effects.OfType<StopSession>().Any(s => s.Path == path);

    private SupervisorResult Advance(double seconds)
    {
        _time.Advance(TimeSpan.FromSeconds(seconds));
        return _sup.Tick();
    }

    /// <summary>Advances one second at a time until a StartSession for the path appears; returns seconds waited.</summary>
    private int AdvanceUntilStart(string path, int maxSeconds = 1000)
    {
        for (var s = 1; s <= maxSeconds; s++)
        {
            if (Starts(Advance(1), path))
                return s;
        }
        throw new Xunit.Sdk.XunitException($"no start for {path} within {maxSeconds} s");
    }

    private int Launched(string path)
    {
        var pid = _nextPid++;
        _sup.SessionStarted(path, pid);
        return pid;
    }

    private SupervisorResult Load(params FolderEntry[] folders) => _sup.Load(folders);

    // ---- host start ----------------------------------------------------------------------

    [Fact]
    public void Host_start_starts_every_on_folder_staggered_by_one_second()
    {
        var r = Load(new FolderEntry(A, true), new FolderEntry(B, false), new FolderEntry(C, true));

        Assert.True(Starts(r, A));
        Assert.False(Starts(r, C));
        Assert.Equal(FolderStatus.Starting, Status(A));
        Assert.Equal(FolderStatus.Off, Status(B));
        Assert.Equal(FolderStatus.Starting, Status(C));

        Assert.False(Starts(Advance(0.5), C));
        Assert.True(Starts(Advance(0.5), C));
        Assert.Empty(Advance(10).Effects.OfType<StartSession>());
    }

    [Fact]
    public void Loaded_folders_keep_their_order_and_saved_state()
    {
        Load(new FolderEntry(B, false), new FolderEntry(A, true));
        Assert.Equal([B, A], _sup.Folders.Select(f => f.Path));
        Assert.All(_sup.Folders, f => Assert.True(f.Saved));
        Assert.True(_sup.Get(A)!.On);
        Assert.False(_sup.Get(B)!.On);
    }

    // ---- starting -> running -------------------------------------------------------------

    [Fact]
    public void Ready_output_makes_a_starting_session_running()
    {
        Load(new FolderEntry(A, true));
        var pid = Launched(A);
        Assert.Equal(FolderStatus.Starting, Status(A));
        Assert.Equal(pid, _sup.Get(A)!.Pid);
        Assert.Equal(_t0, _sup.Get(A)!.StartedAt);

        var r = _sup.ReadySeen(A);
        Assert.Equal(FolderStatus.Running, Status(A));
        var change = Assert.Single(r.Effects.OfType<FolderChanged>());
        Assert.Equal(FolderStatus.Starting, change.PreviousStatus);
        Assert.Equal(FolderStatus.Running, change.Folder.Status);
    }

    [Fact]
    public void Starting_becomes_running_after_30_seconds_without_ready_output()
    {
        Load(new FolderEntry(A, true));
        Launched(A);
        Advance(29);
        Assert.Equal(FolderStatus.Starting, Status(A));
        var r = Advance(1);
        Assert.Equal(FolderStatus.Running, Status(A));
        Assert.Contains(r.Effects.OfType<FolderChanged>(), c => c.Folder.Path == A && c.Folder.Status == FolderStatus.Running);
    }

    // ---- backoff -------------------------------------------------------------------------

    [Fact]
    public void Quick_exits_back_off_5_10_20_40_then_fail_on_the_fifth()
    {
        Load(new FolderEntry(A, true));
        int[] expected = [5, 10, 20, 40];
        foreach (var delay in expected)
        {
            Launched(A);
            var exit = _sup.SessionExited(A, 1);
            Assert.False(Starts(exit, A));
            Assert.Equal(FolderStatus.Restarting, Status(A));
            Assert.Equal(_time.GetUtcNow().AddSeconds(delay), _sup.Get(A)!.RestartAt);
            Assert.Equal(delay, AdvanceUntilStart(A));
        }

        Launched(A);
        var fifth = _sup.SessionExited(A, 3);
        Assert.Equal(FolderStatus.Failing, Status(A));
        Assert.Equal(3, _sup.Get(A)!.LastExitCode);
        Assert.Equal(5, _sup.Get(A)!.Crashes10m);
        Assert.Single(fifth.Effects.OfType<Notify>(), n => n.Path == A && n.Status == FolderStatus.Failing);

        Assert.Empty(Advance(3600).Effects.OfType<StartSession>());
        Assert.Equal(FolderStatus.Failing, Status(A));
    }

    [Fact]
    public void Backoff_continues_80_160_then_300_for_every_later_attempt()
    {
        // Sessions that live 9 minutes: never 5 exits in 10 minutes, never a 10-minute reset.
        Load(new FolderEntry(A, true));
        int[] expected = [5, 10, 20, 40, 80, 160, 300, 300, 300];
        foreach (var delay in expected)
        {
            Launched(A);
            Advance(9 * 60);
            _sup.SessionExited(A, 1);
            Assert.Equal(FolderStatus.Restarting, Status(A));
            Assert.Equal(delay, AdvanceUntilStart(A));
        }
    }

    [Fact]
    public void Ten_minutes_up_resets_backoff_and_crash_count()
    {
        Load(new FolderEntry(A, true));
        Launched(A);
        _sup.SessionExited(A, 1);
        Assert.Equal(5, AdvanceUntilStart(A));
        Launched(A);
        _sup.SessionExited(A, 1);
        Assert.Equal(10, AdvanceUntilStart(A));
        Assert.Equal(2, _sup.Get(A)!.Crashes10m);

        Launched(A);
        Advance(10 * 60);
        Assert.Equal(0, _sup.Get(A)!.Crashes10m);
        _sup.SessionExited(A, 1);
        Assert.Equal(1, _sup.Get(A)!.Crashes10m);
        Assert.Equal(5, AdvanceUntilStart(A));
    }

    [Fact]
    public void Failing_counts_exits_in_a_sliding_ten_minute_window()
    {
        Load(new FolderEntry(A, true));
        for (var i = 0; i < 4; i++)
        {
            Launched(A);
            _sup.SessionExited(A, 1);
            AdvanceUntilStart(A);
        }
        // Exits at about 0, 5, 15 and 35 s. The fifth comes 9 minutes after the last start,
        // when the first exits have left the window.
        Launched(A);
        Advance(9 * 60);
        _sup.SessionExited(A, 1);
        Assert.Equal(FolderStatus.Restarting, Status(A));
        Assert.True(_sup.Get(A)!.Crashes10m < 5);
    }

    [Fact]
    public void Clean_exit_code_zero_is_still_an_exit()
    {
        Load(new FolderEntry(A, true));
        Launched(A);
        _sup.SessionExited(A, 0);
        Assert.Equal(FolderStatus.Restarting, Status(A));
        Assert.Equal(1, _sup.Get(A)!.Crashes10m);
        Assert.Equal(0, _sup.Get(A)!.LastExitCode);
    }

    [Fact]
    public void Restart_clears_failing_and_starts_at_once()
    {
        Load(new FolderEntry(A, true));
        for (var i = 0; i < 5; i++)
        {
            Launched(A);
            _sup.SessionExited(A, 1);
            if (i < 4) AdvanceUntilStart(A);
        }
        Assert.Equal(FolderStatus.Failing, Status(A));

        var r = _sup.Restart(A);
        Assert.True(r.Ok);
        Assert.True(Starts(r, A));
        Assert.Equal(0, _sup.Get(A)!.Crashes10m);
        Launched(A);
        _sup.SessionExited(A, 1);
        Assert.Equal(5, AdvanceUntilStart(A)); // backoff starts over too
    }

    [Fact]
    public void Restart_of_a_running_session_stops_it_then_starts_again_without_counting()
    {
        Load(new FolderEntry(A, true));
        Launched(A);
        _sup.ReadySeen(A);

        var r = _sup.Restart(A);
        Assert.True(Stops(r, A));
        Assert.False(Starts(r, A));

        var exit = _sup.SessionExited(A, 0);
        Assert.True(Starts(exit, A));
        Assert.Equal(0, _sup.Get(A)!.Crashes10m);
    }

    [Fact]
    public void Restart_of_an_off_folder_is_refused()
    {
        Load(new FolderEntry(A, false));
        var r = _sup.Restart(A);
        Assert.False(r.Ok);
        Assert.Empty(r.Effects);
    }

    // ---- missing -------------------------------------------------------------------------

    [Fact]
    public void Missing_folder_is_rechecked_every_10_s_for_5_minutes_then_every_60_s()
    {
        _existing.Remove(A);
        var r = Load(new FolderEntry(A, true));
        Assert.False(Starts(r, A));
        Assert.Equal(FolderStatus.Missing, Status(A));
        Assert.Single(r.Effects.OfType<Notify>(), n => n.Status == FolderStatus.Missing);

        var notifies = 0;
        for (var s = 0; s < 8 * 60; s++)
            notifies += Advance(1).Effects.OfType<Notify>().Count();
        Assert.Equal(0, notifies); // once per transition, not per check

        var times = _existsChecks.Where(c => c.Path == A).Select(c => (int)c.At.TotalSeconds).ToList();
        var expected = Enumerable.Range(0, 31).Select(i => i * 10).Concat([360, 420, 480]).ToList();
        Assert.Equal(expected, times);
        Assert.Equal(0, _sup.Get(A)!.Crashes10m);
    }

    [Fact]
    public void Missing_folder_starts_as_soon_as_it_appears()
    {
        _existing.Remove(A);
        Load(new FolderEntry(A, true));
        Advance(5);
        _existing.Add(A);
        Assert.Equal(5, AdvanceUntilStart(A));
        Assert.Equal(FolderStatus.Starting, Status(A));
    }

    [Fact]
    public void Folder_that_vanishes_before_a_restart_shows_missing()
    {
        Load(new FolderEntry(A, true));
        Launched(A);
        _sup.SessionExited(A, 1);
        _existing.Remove(A);
        Advance(5);
        Assert.Equal(FolderStatus.Missing, Status(A));
        Assert.Equal(1, _sup.Get(A)!.Crashes10m);
    }

    // ---- start failures ------------------------------------------------------------------

    [Fact]
    public void Start_failure_shows_the_error_and_retries_every_60_s_without_counting()
    {
        Load(new FolderEntry(A, true));
        _sup.StartFailed(A, "claude not found: install Claude Code or set its path in Settings");
        var f = _sup.Get(A)!;
        Assert.Equal(FolderStatus.Restarting, f.Status);
        Assert.Equal("claude not found: install Claude Code or set its path in Settings", f.Error);
        Assert.Equal(0, f.Crashes10m);

        Assert.Equal(60, AdvanceUntilStart(A));
        _sup.StartFailed(A, "still not found");
        Assert.Equal(60, AdvanceUntilStart(A));
        Assert.Equal(0, _sup.Get(A)!.Crashes10m);

        Launched(A);
        Assert.Null(_sup.Get(A)!.Error);
    }

    // ---- prompts -------------------------------------------------------------------------

    [Fact]
    public void Prompt_makes_the_folder_waiting_and_notifies_once_per_transition()
    {
        Load(new FolderEntry(A, true));
        Launched(A);
        _sup.ReadySeen(A);

        var r = _sup.PromptChanged(A, "Choose [1/2] (default: 1):");
        Assert.Equal(FolderStatus.Waiting, Status(A));
        Assert.Equal("Choose [1/2] (default: 1):", _sup.Get(A)!.Prompt);
        Assert.Single(r.Effects.OfType<Notify>(), n => n.Status == FolderStatus.Waiting);
        Assert.Contains(r.Effects.OfType<FolderLog>(), l => l.Message.Contains("Choose [1/2]"));

        Assert.Empty(_sup.PromptChanged(A, "Choose [1/2] (default: 1):").Effects);

        _sup.PromptChanged(A, null);
        Assert.Equal(FolderStatus.Running, Status(A));
        Assert.Null(_sup.Get(A)!.Prompt);

        Assert.Single(_sup.PromptChanged(A, "Trust x? [y/N]").Effects.OfType<Notify>());
    }

    [Fact]
    public void Prompt_while_starting_is_waiting_too()
    {
        Load(new FolderEntry(A, true));
        Launched(A);
        _sup.PromptChanged(A, "Trust x? [y/N]");
        Assert.Equal(FolderStatus.Waiting, Status(A));
        Advance(60);
        Assert.Equal(FolderStatus.Waiting, Status(A));
    }

    // ---- on / off ------------------------------------------------------------------------

    [Fact]
    public void Turning_off_stops_the_session_and_its_exit_is_not_counted()
    {
        Load(new FolderEntry(A, true));
        Launched(A);

        var off = _sup.SetOn(A, false);
        Assert.True(off.Ok);
        Assert.True(Stops(off, A));
        Assert.Equal(FolderStatus.Off, Status(A));
        Assert.Equal([new FolderEntry(A, false)], Assert.Single(off.Effects.OfType<FoldersChanged>()).Folders);

        var exit = _sup.SessionExited(A, 0);
        Assert.False(Starts(exit, A));
        Assert.Equal(0, _sup.Get(A)!.Crashes10m);
        Assert.Empty(Advance(600).Effects.OfType<StartSession>());
    }

    [Fact]
    public void Turning_on_again_before_the_stop_finishes_starts_after_the_exit()
    {
        Load(new FolderEntry(A, true));
        Launched(A);
        _sup.SetOn(A, false);
        var on = _sup.SetOn(A, true);
        Assert.False(Starts(on, A));
        Assert.True(Starts(_sup.SessionExited(A, 0), A));
    }

    [Fact]
    public void Turning_off_while_launching_stops_the_session_once_it_has_started()
    {
        Load(new FolderEntry(A, true));
        _sup.SetOn(A, false);
        var started = _sup.SessionStarted(A, 7);
        Assert.True(Stops(started, A));
        Assert.Equal(FolderStatus.Off, Status(A));
    }

    [Fact]
    public void Turning_off_a_restarting_folder_cancels_the_restart()
    {
        Load(new FolderEntry(A, true));
        Launched(A);
        _sup.SessionExited(A, 1);
        _sup.SetOn(A, false);
        Assert.Equal(FolderStatus.Off, Status(A));
        Assert.Empty(Advance(600).Effects.OfType<StartSession>());
    }

    [Fact]
    public void Turning_on_starts_immediately_and_saves()
    {
        Load(new FolderEntry(A, false));
        var r = _sup.SetOn(A, true);
        Assert.True(Starts(r, A));
        Assert.Equal([new FolderEntry(A, true)], Assert.Single(r.Effects.OfType<FoldersChanged>()).Folders);
    }

    [Fact]
    public void Set_on_an_unknown_folder_is_refused()
    {
        var r = _sup.SetOn(A, true);
        Assert.False(r.Ok);
        Assert.NotNull(r.Error);
    }

    [Fact]
    public void Lookups_ignore_case()
    {
        Load(new FolderEntry(A, false));
        Assert.True(_sup.SetOn(A.ToUpperInvariant(), true).Ok);
        Assert.Equal(A, _sup.Get(A.ToUpperInvariant())!.Path);
    }

    // ---- add / remove --------------------------------------------------------------------

    [Fact]
    public void Add_saves_a_new_folder_as_on_and_starts_it()
    {
        Load(new FolderEntry(A, false));
        var r = _sup.Add(B);
        Assert.True(r.Ok);
        Assert.True(Starts(r, B));
        Assert.Equal([new FolderEntry(A, false), new FolderEntry(B, true)], Assert.Single(r.Effects.OfType<FoldersChanged>()).Folders);
    }

    [Fact]
    public void Add_of_an_existing_folder_just_turns_it_on()
    {
        Load(new FolderEntry(A, false));
        var r = _sup.Add(A);
        Assert.True(Starts(r, A));
        Assert.Single(_sup.Folders);
        Assert.True(_sup.Get(A)!.On);
    }

    [Fact]
    public void Remove_only_works_when_off()
    {
        Load(new FolderEntry(A, true), new FolderEntry(B, false));
        Assert.False(_sup.Remove(A).Ok);

        var r = _sup.Remove(B);
        Assert.True(r.Ok);
        Assert.Null(_sup.Get(B));
        Assert.Equal([new FolderEntry(A, true)], Assert.Single(r.Effects.OfType<FoldersChanged>()).Folders);
        Assert.Single(r.Effects.OfType<FolderRemoved>(), e => e.Path == B);
        Assert.False(_sup.Remove(B).Ok);
    }

    // ---- external sessions ---------------------------------------------------------------

    [Fact]
    public void External_session_in_an_unlisted_folder_shows_as_an_unsaved_row()
    {
        Load();
        _sup.ExternalChanged(C, 555);
        var f = _sup.Get(C)!;
        Assert.Equal(FolderStatus.External, f.Status);
        Assert.False(f.On);
        Assert.False(f.Saved);
        Assert.Equal(555, f.ExternalPid);

        // Turning it ON saves it and leaves the external session running.
        var on = _sup.SetOn(C, true);
        Assert.False(Starts(on, C));
        Assert.Equal(FolderStatus.External, Status(C));
        Assert.Equal([new FolderEntry(C, true)], Assert.Single(on.Effects.OfType<FoldersChanged>()).Folders);

        // When it exits, the host starts its own.
        Assert.True(Starts(_sup.ExternalChanged(C, null), C));
    }

    [Fact]
    public void Unsaved_row_disappears_with_its_external_session()
    {
        Load();
        _sup.ExternalChanged(C, 555);
        var r = _sup.ExternalChanged(C, null);
        Assert.Null(_sup.Get(C));
        Assert.Single(r.Effects.OfType<FolderRemoved>());
        Assert.Empty(r.Effects.OfType<FoldersChanged>());
    }

    [Fact]
    public void Folder_with_an_external_session_at_host_start_is_not_started()
    {
        _sup.ExternalChanged(A, 555);
        var r = Load(new FolderEntry(A, true), new FolderEntry(B, true));
        Assert.False(Starts(r, A));
        Assert.Equal(FolderStatus.External, Status(A));
        Assert.True(_sup.Get(A)!.Saved);
        Assert.True(Starts(Advance(1), B));
        Assert.DoesNotContain(Advance(60).Effects.OfType<StartSession>(), s => s.Path == A);
    }

    [Fact]
    public void Off_folder_whose_external_session_exits_is_not_started()
    {
        Load(new FolderEntry(A, false));
        _sup.ExternalChanged(A, 1);
        Assert.Equal(FolderStatus.External, Status(A));
        var r = _sup.ExternalChanged(A, null);
        Assert.False(Starts(r, A));
        Assert.Equal(FolderStatus.Off, Status(A));
    }

    [Fact]
    public void Take_over_stops_the_external_session_then_starts_a_hosted_one()
    {
        Load(new FolderEntry(A, false));
        _sup.ExternalChanged(A, 555);

        var r = _sup.Takeover(A);
        Assert.True(r.Ok);
        Assert.Equal(new StopExternal(A, 555), Assert.Single(r.Effects.OfType<StopExternal>()));
        Assert.False(Starts(r, A));
        Assert.True(_sup.Get(A)!.On);

        Assert.True(Starts(_sup.ExternalChanged(A, null), A));
    }

    [Fact]
    public void Take_over_without_an_external_session_is_refused()
    {
        Load(new FolderEntry(A, true));
        Assert.False(_sup.Takeover(A).Ok);
    }

    [Fact]
    public void Take_over_of_an_unsaved_row_saves_it_on()
    {
        Load();
        _sup.ExternalChanged(C, 555);

        var r = _sup.Takeover(C);

        Assert.Equal(new StopExternal(C, 555), Assert.Single(r.Effects.OfType<StopExternal>()));
        Assert.Equal([new FolderEntry(C, true)], Assert.Single(r.Effects.OfType<FoldersChanged>()).Folders);
        Assert.Contains(r.Effects.OfType<FolderLog>(), l => l.Path == C && l.Message.Contains("taking over", StringComparison.Ordinal));
        Assert.True(Starts(_sup.ExternalChanged(C, null), C));
    }

    [Fact]
    public void A_failed_take_over_shows_its_error_and_keeps_the_row_external()
    {
        Load(new FolderEntry(A, false));
        _sup.ExternalChanged(A, 555);
        _sup.Takeover(A);

        var r = _sup.TakeoverFailed(A, "access denied");

        Assert.True(r.Ok);
        var f = _sup.Get(A)!;
        Assert.Equal(FolderStatus.External, f.Status);
        Assert.True(f.On);
        Assert.Contains("access denied", f.Error, StringComparison.Ordinal);
        Assert.Contains(r.Effects.OfType<FolderLog>(), l => l.Message.Contains("access denied", StringComparison.Ordinal));
        Assert.Contains(r.Effects.OfType<FolderChanged>(), c => c.Folder.Path == A);

        // When the external session ends after all, the host starts its own and the error goes.
        Assert.True(Starts(_sup.ExternalChanged(A, null), A));
        _sup.SessionStarted(A, 7);
        Assert.Null(_sup.Get(A)!.Error);
    }

    [Fact]
    public void Take_over_failed_for_an_unknown_folder_is_ignored()
    {
        Load();
        Assert.Empty(_sup.TakeoverFailed(A, "x").Effects);
    }

    [Fact]
    public void Only_take_over_ever_asks_to_stop_an_external_session()
    {
        // A saved ON folder, a saved OFF folder and an unsaved row, each with an external session.
        Load(new FolderEntry(A, true), new FolderEntry(B, false));
        var all = new List<SupervisorResult>
        {
            _sup.ExternalChanged(A, 1),
            _sup.ExternalChanged(B, 2),
            _sup.ExternalChanged(C, 3),
        };
        for (var i = 0; i < 30; i++)
            all.Add(Advance(20));
        all.Add(_sup.SetOn(A, false));
        all.Add(_sup.SetOn(A, true));
        all.Add(_sup.SetOn(B, true));
        all.Add(_sup.SetOn(B, false));
        all.Add(_sup.Remove(B));
        all.Add(_sup.Restart(A));
        all.Add(_sup.ExternalChanged(A, 4)); // the user restarted theirs
        all.Add(_sup.Shutdown());
        all.Add(Advance(60));

        Assert.Empty(all.SelectMany(r => r.Effects).OfType<StopExternal>());
        Assert.Empty(all.SelectMany(r => r.Effects).OfType<StopSession>());
        Assert.Empty(all.SelectMany(r => r.Effects).OfType<StartSession>());
        Assert.False(_sup.Get(C)!.On);
    }

    [Fact]
    public void Take_over_names_only_its_own_folders_external_process()
    {
        Load(new FolderEntry(A, true));
        _sup.ExternalChanged(A, 1);
        _sup.ExternalChanged(C, 3);

        var r = _sup.Takeover(A);

        Assert.Equal([new StopExternal(A, 1)], r.Effects.OfType<StopExternal>());
    }

    [Fact]
    public void An_external_session_that_appears_beside_a_hosted_one_does_not_stop_it()
    {
        Load(new FolderEntry(A, true));
        Advance(1);
        Launched(A);
        _sup.ReadySeen(A);

        var r = _sup.ExternalChanged(A, 99);

        Assert.Empty(r.Effects.OfType<StopSession>());
        Assert.Empty(r.Effects.OfType<StopExternal>());
        Assert.Equal(FolderStatus.Running, Status(A));
    }

    // ---- shutdown and stale events -------------------------------------------------------

    [Fact]
    public void Shutdown_stops_every_session_and_starts_nothing_more()
    {
        Load(new FolderEntry(A, true), new FolderEntry(B, true));
        Launched(A);
        var r = _sup.Shutdown();
        Assert.True(Stops(r, A));
        Assert.Empty(r.Effects.OfType<FoldersChanged>());
        Assert.Empty(Advance(60).Effects.OfType<StartSession>());
        Assert.False(Starts(_sup.SessionExited(A, 0), A));
        Assert.True(_sup.Get(A)!.On); // desired state is kept for the next host start
    }

    [Fact]
    public void Events_for_unknown_folders_or_dead_sessions_are_ignored()
    {
        Load(new FolderEntry(A, true));
        Assert.Empty(_sup.SessionExited(B, 1).Effects);
        Assert.Empty(_sup.ReadySeen(B).Effects);
        Assert.Empty(_sup.PromptChanged(B, "x [y/n]:").Effects);
        Launched(A);
        _sup.SessionExited(A, 1);
        Assert.Empty(_sup.SessionExited(A, 1).Effects);
        Assert.Equal(1, _sup.Get(A)!.Crashes10m);
    }

    // ---- logs, rows, next due ------------------------------------------------------------

    [Fact]
    public void Starts_exits_and_restarts_are_logged_per_folder()
    {
        Load(new FolderEntry(A, true));
        var started = _sup.SessionStarted(A, 42);
        Assert.Contains(started.Effects.OfType<FolderLog>(), l => l.Path == A && l.Message.Contains("42"));
        var exit = _sup.SessionExited(A, -1073741510);
        var log = string.Join("\n", exit.Effects.OfType<FolderLog>().Select(l => l.Message));
        Assert.Contains("-1073741510", log);
        Assert.Contains("5 s", log);
    }

    [Fact]
    public void Rows_carry_names_countdown_and_exit_code()
    {
        Load(new FolderEntry(A, true), new FolderEntry(@"D:\other\a", false));
        Launched(A);
        _sup.SessionExited(A, 9);
        Advance(2);

        var rows = _sup.GetRows();
        var a = rows.Single(r => r.Path == A);
        Assert.Equal(@"src\a", a.Name);
        Assert.Equal(FolderStatus.Restarting, a.Status);
        Assert.Equal(3, a.RestartInS);
        Assert.Equal(9, a.ExitCode);
        Assert.Equal(1, a.Crashes10m);
        Assert.Null(a.Pid);
        Assert.True(a.On);
        Assert.True(a.Saved);

        var other = rows.Single(r => r.Path == @"D:\other\a");
        Assert.Equal(@"other\a", other.Name);
        Assert.Equal(FolderStatus.Off, other.Status);
        Assert.Null(other.RestartInS);
        Assert.Null(other.ExitCode);
    }

    [Fact]
    public void Running_row_has_pid_and_start_time()
    {
        Load(new FolderEntry(A, true));
        var pid = Launched(A);
        _sup.ReadySeen(A);
        var row = Assert.Single(_sup.GetRows());
        Assert.Equal(pid, row.Pid);
        Assert.Equal(_t0, row.StartedAt);
        Assert.Equal("a", row.Name);
    }

    [Fact]
    public void Next_due_is_the_earliest_timer()
    {
        Load(new FolderEntry(A, true));
        Launched(A);
        Assert.Equal(_t0.AddSeconds(30), _sup.NextDueAt);
        _sup.ReadySeen(A);
        _sup.SessionExited(A, 1);
        Assert.Equal(_t0.AddSeconds(5), _sup.NextDueAt);
    }
}
