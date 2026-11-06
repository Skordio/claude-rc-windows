using ClaudeRc.Core;
using ClaudeRc.Tray.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace ClaudeRc.Tray.Tests;

public class FolderRowViewModelTests
{
    private const string P = @"C:\src\app";
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-11-06T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));

    private FolderRowViewModel Vm(FolderStatus status, bool on = true, bool saved = true, string? prompt = null,
        int? exitCode = null, int? restartInS = null, string? error = null, IReadOnlyList<string>? lastLines = null) =>
        new(TestRows.Make(P, status, on, saved, prompt: prompt, exitCode: exitCode, restartInS: restartInS, error: error, lastLines: lastLines), _time);

    [Theory]
    [InlineData(FolderStatus.Off, false, "off", "Grey")]
    [InlineData(FolderStatus.Starting, true, "starting", "Yellow")]
    [InlineData(FolderStatus.Running, true, "running", "Green")]
    [InlineData(FolderStatus.Waiting, true, "waiting for input", "Yellow")]
    [InlineData(FolderStatus.Missing, true, "folder missing", "Red")]
    [InlineData(FolderStatus.External, false, "running (started outside the app)", "Blue")]
    public void Status_text_and_colour_follow_spec(FolderStatus status, bool on, string text, string colour)
    {
        var vm = Vm(status, on);
        Assert.Equal(text, vm.StatusText);
        Assert.Equal(colour, vm.StatusColor.ToString());
    }

    [Fact]
    public void Name_and_full_path_come_from_the_row()
    {
        var vm = new FolderRowViewModel(TestRows.Make(@"D:\work\app", FolderStatus.Running, name: @"work\app"), _time);
        Assert.Equal(@"work\app", vm.Name);
        Assert.Equal(@"D:\work\app", vm.Path);
    }

    [Fact]
    public void Restarting_shows_countdown_and_last_exit_code()
    {
        var vm = Vm(FolderStatus.Restarting, exitCode: 3, restartInS: 10);
        Assert.Equal("restarting in 10s (exit code 3)", vm.StatusText);
        Assert.Equal(StatusColor.Yellow, vm.StatusColor);
    }

    [Fact]
    public void Restarting_countdown_runs_down_locally_between_row_updates()
    {
        var vm = Vm(FolderStatus.Restarting, exitCode: 1, restartInS: 5);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        _time.Advance(TimeSpan.FromSeconds(1));
        vm.Tick();
        Assert.Equal("restarting in 4s (exit code 1)", vm.StatusText);
        Assert.Contains(nameof(vm.StatusText), changed);

        _time.Advance(TimeSpan.FromSeconds(3.5));
        vm.Tick();
        Assert.Equal("restarting in 1s (exit code 1)", vm.StatusText);

        _time.Advance(TimeSpan.FromSeconds(10));
        vm.Tick();
        Assert.Equal("restarting now (exit code 1)", vm.StatusText);
    }

    [Fact]
    public void Tick_does_not_raise_change_when_the_text_is_the_same()
    {
        var vm = Vm(FolderStatus.Running);
        var changed = 0;
        vm.PropertyChanged += (_, _) => changed++;
        _time.Advance(TimeSpan.FromSeconds(1));
        vm.Tick();
        Assert.Equal(0, changed);
    }

    [Fact]
    public void A_new_row_restarts_the_countdown_from_its_value()
    {
        var vm = Vm(FolderStatus.Restarting, exitCode: 1, restartInS: 5);
        _time.Advance(TimeSpan.FromSeconds(4));
        vm.Update(TestRows.Make(P, FolderStatus.Restarting, exitCode: 2, restartInS: 20));
        Assert.Equal("restarting in 20s (exit code 2)", vm.StatusText);
    }

    [Fact]
    public void Restarting_after_a_start_error_has_no_exit_code()
    {
        var vm = Vm(FolderStatus.Restarting, restartInS: 60, error: "claude not found: install Claude Code or set its path in Settings");
        Assert.Equal("restarting in 60s", vm.StatusText);
        Assert.True(vm.HasError);
        Assert.Equal("claude not found: install Claude Code or set its path in Settings", vm.ErrorText);
    }

    [Fact]
    public void Failing_shows_last_exit_code_and_last_lines()
    {
        var vm = Vm(FolderStatus.Failing, exitCode: 1, lastLines: ["error: boom", "bye"]);
        Assert.Equal("failing (exit code 1)", vm.StatusText);
        Assert.Equal(StatusColor.Red, vm.StatusColor);
        Assert.True(vm.HasLastLines);
        Assert.Equal("error: boom\nbye", vm.LastLinesText);
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(1, "1")]
    [InlineData(255, "255")]
    [InlineData(-1, "-1 (0xFFFFFFFF)")]
    [InlineData(-1073741510, "-1073741510 (0xC000013A)")]
    [InlineData(3221225786, "-1073741510 (0xC000013A)")]
    public void Exit_codes_that_are_ntstatus_values_also_show_hex(long code, string text)
    {
        Assert.Equal(text, FolderRowViewModel.FormatExitCode(unchecked((int)code)));
    }

    [Fact]
    public void Waiting_shows_prompt_line()
    {
        var vm = Vm(FolderStatus.Waiting, prompt: "Choose [1/2] (default: 1):");
        Assert.True(vm.HasPrompt);
        Assert.Equal("Choose [1/2] (default: 1):", vm.PromptText);
        Assert.Equal("Show to answer it", FolderRowViewModel.PromptHint);
    }

    [Fact]
    public void Running_has_no_prompt_error_or_last_lines()
    {
        var vm = Vm(FolderStatus.Running);
        Assert.False(vm.HasPrompt);
        Assert.False(vm.HasError);
        Assert.False(vm.HasLastLines);
    }

    [Theory]
    [InlineData(FolderStatus.Starting, true)]
    [InlineData(FolderStatus.Running, true)]
    [InlineData(FolderStatus.Waiting, true)]
    [InlineData(FolderStatus.Restarting, false)]
    [InlineData(FolderStatus.Failing, false)]
    [InlineData(FolderStatus.Missing, false)]
    [InlineData(FolderStatus.External, false)]
    [InlineData(FolderStatus.Off, false)]
    public void Show_is_enabled_only_when_there_is_a_hosted_console(FolderStatus status, bool canShow)
    {
        Assert.Equal(canShow, Vm(status, on: status != FolderStatus.Off).CanShow);
    }

    [Fact]
    public void Show_is_disabled_for_external_rows_even_when_on()
    {
        Assert.False(Vm(FolderStatus.External, on: true).CanShow);
    }

    [Theory]
    [InlineData(FolderStatus.Off, false, true, true)]
    [InlineData(FolderStatus.Running, true, true, false)]
    [InlineData(FolderStatus.Failing, true, true, false)]
    [InlineData(FolderStatus.External, true, true, false)]
    [InlineData(FolderStatus.External, false, false, false)]
    public void Remove_only_when_off_and_saved(FolderStatus status, bool on, bool saved, bool canRemove)
    {
        Assert.Equal(canRemove, Vm(status, on, saved).CanRemove);
    }

    [Theory]
    [InlineData(FolderStatus.Failing, true)]
    [InlineData(FolderStatus.Restarting, true)]
    [InlineData(FolderStatus.Running, false)]
    [InlineData(FolderStatus.Waiting, false)]
    [InlineData(FolderStatus.Starting, false)]
    [InlineData(FolderStatus.Missing, false)]
    [InlineData(FolderStatus.External, false)]
    public void Restart_button_shows_when_failing_or_stopped_unexpectedly(FolderStatus status, bool shown)
    {
        Assert.Equal(shown, Vm(status, exitCode: 1, restartInS: 5).ShowRestartButton);
    }

    [Theory]
    [InlineData(FolderStatus.Running, true, true)]
    [InlineData(FolderStatus.Missing, true, true)]
    [InlineData(FolderStatus.Waiting, true, true)]
    [InlineData(FolderStatus.Off, false, false)]
    [InlineData(FolderStatus.External, true, false)]
    public void Restart_in_the_row_menu_while_on(FolderStatus status, bool on, bool canRestart)
    {
        Assert.Equal(canRestart, Vm(status, on).CanRestart);
    }

    [Theory]
    [InlineData(FolderStatus.External, true)]
    [InlineData(FolderStatus.Running, false)]
    [InlineData(FolderStatus.Off, false)]
    public void Take_over_only_for_external_rows(FolderStatus status, bool shown)
    {
        Assert.Equal(shown, Vm(status, on: status != FolderStatus.Off).ShowTakeOver);
    }

    [Theory]
    [InlineData(FolderStatus.Starting, true)]
    [InlineData(FolderStatus.Running, true)]
    [InlineData(FolderStatus.Waiting, true)]
    [InlineData(FolderStatus.Restarting, false)]
    [InlineData(FolderStatus.Failing, false)]
    [InlineData(FolderStatus.Missing, false)]
    [InlineData(FolderStatus.External, false)]
    public void Turning_off_a_running_folder_needs_confirmation(FolderStatus status, bool confirm)
    {
        var message = Vm(status).TurnOffConfirmation;
        if (confirm)
        {
            Assert.NotNull(message);
            Assert.Contains("stops the running claude rc session; it won't come back at logon/boot", message, StringComparison.Ordinal);
        }
        else
        {
            Assert.Null(message);
        }
    }

    [Fact]
    public void An_unsaved_row_is_marked_and_says_what_turning_it_on_does()
    {
        var vm = Vm(FolderStatus.External, on: false, saved: false);

        Assert.True(vm.IsUnsaved);
        Assert.Equal("not in your list", vm.UnsavedText);
        Assert.Contains("adds the folder to your list", vm.ToggleToolTip, StringComparison.Ordinal);
        Assert.Contains("keeps running", vm.ToggleToolTip, StringComparison.Ordinal);
        Assert.Equal("running (started outside the app)", vm.StatusText);
        Assert.Equal(StatusColor.Blue, vm.StatusColor);
    }

    [Theory]
    [InlineData(true, "Turn off ")]
    [InlineData(false, "Turn on ")]
    public void Row_buttons_have_accessible_names_with_the_folder_name(bool on, string togglePrefix)
    {
        var vm = Vm(on ? FolderStatus.Running : FolderStatus.Off, on, saved: true);

        Assert.Equal("Show " + vm.Name, vm.ShowAccessibleName);
        Assert.Equal(togglePrefix + vm.Name, vm.ToggleAccessibleName);
        Assert.Equal("More actions for " + vm.Name, vm.MoreAccessibleName);
        Assert.Equal("Take over " + vm.Name, vm.TakeOverAccessibleName);
        Assert.Equal("Restart " + vm.Name, vm.RestartAccessibleName);
    }

    [Theory]
    [InlineData(FolderStatus.External, true)]
    [InlineData(FolderStatus.Running, true)]
    [InlineData(FolderStatus.Off, false)]
    public void A_saved_row_is_not_marked(FolderStatus status, bool on)
    {
        var vm = Vm(status, on, saved: true);
        Assert.False(vm.IsUnsaved);
        Assert.Null(vm.UnsavedText);
        Assert.Equal("Turn ON or OFF", vm.ToggleToolTip);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void An_external_row_enables_take_over_and_disables_show(bool on, bool saved)
    {
        var vm = Vm(FolderStatus.External, on, saved);
        Assert.True(vm.TakeOverCommand.CanExecute(null));
        Assert.False(vm.ShowCommand.CanExecute(null));
        Assert.False(vm.RestartCommand.CanExecute(null));
        Assert.Null(vm.TurnOffConfirmation); // OFF does not stop the external session
    }

    [Fact]
    public void Update_raises_changes_for_saving_an_unsaved_row()
    {
        var vm = Vm(FolderStatus.External, on: false, saved: false);
        var changed = new HashSet<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.Update(TestRows.Make(P, FolderStatus.External, on: true, saved: true));

        Assert.Contains(nameof(vm.IsUnsaved), changed);
        Assert.Contains(nameof(vm.UnsavedText), changed);
        Assert.Contains(nameof(vm.ToggleToolTip), changed);
        Assert.False(vm.IsUnsaved);
    }

    [Fact]
    public void Update_raises_changes_for_the_new_status()
    {
        var vm = Vm(FolderStatus.Running);
        var changed = new HashSet<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        vm.Update(TestRows.Make(P, FolderStatus.Off, on: false));
        Assert.Contains(nameof(vm.StatusText), changed);
        Assert.Contains(nameof(vm.IsOn), changed);
        Assert.Contains(nameof(vm.CanRemove), changed);
        Assert.False(vm.IsOn);
        Assert.Equal("off", vm.StatusText);
    }
}
