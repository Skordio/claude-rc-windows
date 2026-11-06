using System.Text;
using ClaudeRc.Core.Prompts;
using ClaudeRc.Host.Terminal;

namespace ClaudeRc.Host.Tests;

public sealed class TerminalEmulatorTests
{
    private const string Esc = "\u001b";

    private static TerminalEmulator Term(int cols = 20, int rows = 5, int scrollback = TerminalEmulator.DefaultScrollback) =>
        new(cols, rows, scrollback);

    private static string[] Texts(IReadOnlyList<ScreenRow> rows) => [.. rows.Select(r => r.Text)];

    private static TerminalEmulator Fed(string text, int cols = 20, int rows = 5)
    {
        var t = Term(cols, rows);
        t.Feed(text);
        return t;
    }

    // ---- printing, wrapping, cursor ----

    [Fact]
    public void Prints_text_and_handles_cr_lf()
    {
        var t = Fed("hello\r\nworld");
        Assert.Equal(["hello", "world", "", "", ""], Texts(t.GetScreen()));
        Assert.Equal((1, 5), (t.CursorRow, t.CursorColumn));
    }

    [Fact]
    public void Lf_without_cr_keeps_the_column()
    {
        var t = Fed("ab\ncd");
        Assert.Equal(["ab", "  cd"], Texts(t.GetScreen())[..2]);
    }

    [Fact]
    public void Long_line_soft_wraps_and_marks_the_row()
    {
        var t = Fed("0123456789ABCDE", cols: 10);
        var rows = t.GetScreen();
        Assert.Equal("0123456789", rows[0].Text);
        Assert.True(rows[0].Wrapped);
        Assert.Equal("ABCDE", rows[1].Text);
        Assert.False(rows[1].Wrapped);
    }

    [Fact]
    public void Exactly_full_row_does_not_wrap_until_the_next_character()
    {
        var t = Fed("0123456789", cols: 10);
        Assert.False(t.GetScreen()[0].Wrapped);
        Assert.Equal((0, 9), (t.CursorRow, t.CursorColumn));
        t.Feed("\r\n");
        Assert.False(t.GetScreen()[0].Wrapped);
        Assert.Equal((1, 0), (t.CursorRow, t.CursorColumn));
    }

    [Fact]
    public void Erasing_the_end_of_a_row_clears_its_wrap_flag()
    {
        var t = Fed("0123456789AB", cols: 10);
        t.Feed($"{Esc}[1;3H{Esc}[K");
        Assert.False(t.GetScreen()[0].Wrapped);
        Assert.Equal("01", t.GetScreen()[0].Text);
    }

    [Fact]
    public void Absolute_and_relative_cursor_moves()
    {
        var t = Fed($"{Esc}[3;4HX{Esc}[HY{Esc}[2BZ{Esc}[1;10H{Esc}[3DW{Esc}[5GV{Esc}[2dU");
        Assert.Equal(["Y   V W", "     U", " Z X", "", ""], Texts(t.GetScreen()));
    }

    [Fact]
    public void Cursor_moves_are_clamped_to_the_screen()
    {
        var t = Fed($"{Esc}[99;99HX{Esc}[99AY{Esc}[99DZ", cols: 10, rows: 3);
        var rows = Texts(t.GetScreen());
        Assert.Equal("Z", rows[0].TrimEnd()[..1]);
        Assert.Equal("X", rows[2].Trim());
    }

    [Fact]
    public void Backspace_and_tab()
    {
        var t = Fed("abc\bX\tY");
        Assert.Equal("abX     Y", t.GetScreen()[0].Text);
    }

    // ---- erase ----

    [Fact]
    public void Erase_in_line_variants()
    {
        var t = Fed($"0123456789{Esc}[1;5H{Esc}[1K", cols: 10);
        Assert.Equal("     56789", t.GetScreen()[0].Text);
        t.Feed($"{Esc}[1;8H{Esc}[0K");
        Assert.Equal("     56", t.GetScreen()[0].Text);
        t.Feed($"{Esc}[2K");
        Assert.Equal("", t.GetScreen()[0].Text);
    }

    [Fact]
    public void Erase_in_display_variants()
    {
        var t = Fed("aaaa\r\nbbbb\r\ncccc", cols: 4, rows: 3);
        t.Feed($"{Esc}[2;3H{Esc}[0J");
        Assert.Equal(["aaaa", "bb", ""], Texts(t.GetScreen()));
        t.Feed($"{Esc}[1;2H{Esc}[1J");
        Assert.Equal(["  aa", "bb", ""], Texts(t.GetScreen()));
        t.Feed($"{Esc}[2J");
        Assert.Equal(["", "", ""], Texts(t.GetScreen()));
    }

    [Fact]
    public void Erase_insert_and_delete_characters()
    {
        var t = Fed($"0123456789{Esc}[1;3H{Esc}[2X", cols: 10);
        Assert.Equal("01  456789", t.GetScreen()[0].Text);
        t.Feed($"{Esc}[1;3H{Esc}[2P");
        Assert.Equal("01456789", t.GetScreen()[0].Text);
        t.Feed($"{Esc}[1;1H{Esc}[3@");
        Assert.Equal("   0145678", t.GetScreen()[0].Text);
    }

    [Fact]
    public void Insert_and_delete_lines()
    {
        var t = Fed("a\r\nb\r\nc\r\nd", cols: 4, rows: 4);
        t.Feed($"{Esc}[2;1H{Esc}[L");
        Assert.Equal(["a", "", "b", "c"], Texts(t.GetScreen()));
        t.Feed($"{Esc}[2M");
        Assert.Equal(["a", "c", "", ""], Texts(t.GetScreen()));
    }

    // ---- scrolling and scrollback ----

    [Fact]
    public void Lines_scrolled_off_the_top_go_to_the_scrollback()
    {
        var t = Fed("1\r\n2\r\n3\r\n4\r\n5", rows: 3);
        Assert.Equal(["3", "4", "5"], Texts(t.GetScreen()));
        Assert.Equal(["1", "2"], Texts(t.GetScrollback()));
    }

    [Fact]
    public void Scrollback_is_capped()
    {
        var t = Term(cols: 10, rows: 2, scrollback: 5000);
        var sb = new StringBuilder();
        for (var i = 0; i < 6000; i++)
            sb.Append(i).Append("\r\n");
        t.Feed(sb.ToString());
        var back = t.GetScrollback();
        Assert.Equal(5000, back.Count);
        Assert.Equal("999", back[0].Text);
        Assert.Equal("5998", back[^1].Text);
        Assert.Equal(["5999", ""], Texts(t.GetScreen()));
    }

    [Fact]
    public void Scroll_region_scrolls_only_inside_and_keeps_scrollback_clean()
    {
        var t = Fed($"top\r\na\r\nb\r\nbottom{Esc}[2;3r{Esc}[3;1H\r\nc", cols: 10, rows: 4);
        Assert.Equal(["top", "b", "c", "bottom"], Texts(t.GetScreen()));
        Assert.Empty(t.GetScrollback());
    }

    [Fact]
    public void Reverse_index_at_the_top_scrolls_down()
    {
        var t = Fed($"a\r\nb{Esc}[H{Esc}M", cols: 4, rows: 3);
        Assert.Equal(["", "a", "b"], Texts(t.GetScreen()));
    }

    [Fact]
    public void Scroll_up_and_down_commands()
    {
        var t = Fed($"a\r\nb\r\nc{Esc}[S", cols: 4, rows: 3);
        Assert.Equal(["b", "c", ""], Texts(t.GetScreen()));
        t.Feed($"{Esc}[2T");
        Assert.Equal(["", "", "b"], Texts(t.GetScreen()));
    }

    // ---- modes, OSC, strings ----

    [Fact]
    public void Private_modes_from_conhost_are_accepted_and_cursor_visibility_tracked()
    {
        var t = Fed($"{Esc}[?9001h{Esc}[?1004h{Esc}[?25lA{Esc}[?1004l{Esc}[?9001l{Esc}[?2004hB");
        Assert.Equal("AB", t.GetScreen()[0].Text);
        Assert.False(t.CursorVisible);
        t.Feed($"{Esc}[?25h");
        Assert.True(t.CursorVisible);
    }

    [Fact]
    public void Osc_title_is_kept_and_osc_8_hyperlinks_do_not_break_text()
    {
        var t = Fed($"{Esc}]0;C:\\claude.exe\u0007{Esc}]8;id=2;https://claude.ai/code/session_01?from=cli{Esc}\\name{Esc}]8;;{Esc}\\ end");
        Assert.Equal("name end", t.GetScreen()[0].Text);
        Assert.Equal(@"C:\claude.exe", t.Title);
    }

    [Fact]
    public void Dcs_and_apc_strings_are_ignored()
    {
        var t = Fed($"a{Esc}P1$r0m{Esc}\\b{Esc}_payload{Esc}\\c");
        Assert.Equal("abc", t.GetScreen()[0].Text);
    }

    [Fact]
    public void Charset_designation_is_consumed()
    {
        var t = Fed($"{Esc}(Bab{Esc})0c");
        Assert.Equal("abc", t.GetScreen()[0].Text);
    }

    [Fact]
    public void Alternate_screen_saves_and_restores_the_main_screen()
    {
        var t = Fed($"main{Esc}[?1049halt");
        Assert.True(t.AlternateScreen);
        Assert.Equal("alt", t.GetScreen()[0].Text.Trim());
        t.Feed($"{Esc}[?1049l");
        Assert.False(t.AlternateScreen);
        Assert.Equal("main", t.GetScreen()[0].Text);
        Assert.Equal((0, 4), (t.CursorRow, t.CursorColumn));
    }

    [Fact]
    public void Save_and_restore_cursor()
    {
        var t = Fed($"ab{Esc}7{Esc}[3;3Hx{Esc}8c{Esc}[s{Esc}[Hz{Esc}[ud");
        Assert.Equal("zbcd", t.GetScreen()[0].Text);
    }

    [Fact]
    public void Full_reset_clears_everything()
    {
        var t = Fed($"1\r\n2\r\n3\r\n4\r\n5\r\n6{Esc}[?25l{Esc}c", rows: 3);
        Assert.Equal(["", "", ""], Texts(t.GetScreen()));
        Assert.Empty(t.GetScrollback());
        Assert.True(t.CursorVisible);
    }

    // ---- Unicode ----

    [Fact]
    public void Variation_selector_is_zero_width_and_does_not_corrupt_the_line()
    {
        var t = Fed("·✔\uFE0E· Connected · gitB · master", cols: 40);
        Assert.Equal("·✔\uFE0E· Connected · gitB · master", t.GetScreen()[0].Text);
        Assert.Equal(29, t.CursorColumn);
    }

    [Fact]
    public void Wide_characters_take_two_cells()
    {
        var t = Fed("a漢b😀c", cols: 20);
        Assert.Equal("a漢b😀c", t.GetScreen()[0].Text);
        Assert.Equal(7, t.CursorColumn);
    }

    [Fact]
    public void Wide_character_at_the_margin_wraps_whole()
    {
        var t = Fed("123漢", cols: 4);
        Assert.True(t.GetScreen()[0].Wrapped);
        Assert.Equal("123 ", t.GetScreen()[0].Text);
        Assert.Equal("漢", t.GetScreen()[1].Text);
    }

    [Fact]
    public void Overwriting_half_of_a_wide_character_blanks_the_other_half()
    {
        var t = Fed($"漢字{Esc}[1;2Hx");
        Assert.Equal(" x字", t.GetScreen()[0].Text);
    }

    [Fact]
    public void Combining_marks_join_the_previous_cell()
    {
        var t = Fed("e\u0301x");
        Assert.Equal("e\u0301x", t.GetScreen()[0].Text);
        Assert.Equal(2, t.CursorColumn);
    }

    [Fact]
    public void Utf8_split_across_feeds_is_decoded()
    {
        var t = Term();
        var bytes = Encoding.UTF8.GetBytes("a✔😀b");
        foreach (var b in bytes)
            t.Feed([b]);
        Assert.Equal("a✔😀b", t.GetScreen()[0].Text);
    }

    [Fact]
    public void Escape_sequence_split_across_feeds_is_parsed()
    {
        var t = Term();
        foreach (var part in new[] { "ab", "\u001b", "[", "1;", "1H", "X" })
            t.Feed(part);
        Assert.Equal("Xb", t.GetScreen()[0].Text);
    }

    // ---- resize ----

    [Fact]
    public void Resize_narrower_and_shorter_keeps_the_cursor_row_visible()
    {
        var t = Fed("1\r\n2\r\n3\r\n4\r\n5 long line", cols: 20, rows: 5);
        t.Resize(6, 3);
        Assert.Equal((6, 3), (t.Columns, t.Rows));
        Assert.Equal(["3", "4", "5 long"], Texts(t.GetScreen()));
        Assert.Equal(["1", "2"], Texts(t.GetScrollback()));
        Assert.Equal(2, t.CursorRow);
    }

    [Fact]
    public void Resize_drops_blank_rows_below_the_cursor_first()
    {
        var t = Fed("1\r\n2", cols: 20, rows: 5);
        t.Resize(20, 3);
        Assert.Equal(["1", "2", ""], Texts(t.GetScreen()));
        Assert.Empty(t.GetScrollback());
    }

    [Fact]
    public void Resize_taller_adds_blank_rows()
    {
        var t = Fed("1\r\n2", cols: 20, rows: 2);
        t.Resize(30, 4);
        Assert.Equal(["1", "2", "", ""], Texts(t.GetScreen()));
        t.Feed("\r\n3");
        Assert.Equal("3", t.GetScreen()[2].Text);
    }

    // ---- replay serialisation (SPEC 3.3) ----

    [Fact]
    public void Serialize_round_trips_text_styles_wraps_cursor_and_scrollback()
    {
        var t = Term(cols: 10, rows: 4);
        t.Feed($"plain\r\n{Esc}[1;31mred bold{Esc}[0m and {Esc}[38;5;200m256{Esc}[48;2;1;2;3mrgb{Esc}[m\r\n");
        t.Feed("0123456789wrapped\r\n");
        t.Feed($"{Esc}[7minverse{Esc}[27m 漢✔\uFE0E\r\nlast{Esc}[4m");
        t.Feed($"{Esc}[2;5H{Esc}[?25l");
        AssertRoundTrip(t);
    }

    [Fact]
    public void Serialize_round_trips_a_pending_wrap_at_the_margin()
    {
        var t = Fed("0123456789", cols: 10, rows: 3);
        var copy = AssertRoundTrip(t);
        copy.Feed("X");
        Assert.True(copy.GetScreen()[0].Wrapped);
        Assert.Equal("X", copy.GetScreen()[1].Text);
    }

    [Fact]
    public void Serialize_round_trips_a_wrapped_row_followed_by_an_empty_row()
    {
        var t = Fed($"0123456789A{Esc}[2;1H{Esc}[K{Esc}[3;1Hz", cols: 10, rows: 3);
        Assert.True(t.GetScreen()[0].Wrapped);
        AssertRoundTrip(t);
    }

    [Fact]
    public void Serialize_round_trips_the_alternate_screen_and_scroll_region()
    {
        var t = Fed($"1\r\n2\r\n3\r\n4\r\nmain{Esc}[?1049h{Esc}[2;3r{Esc}[3;2Halt", cols: 10, rows: 4);
        var copy = AssertRoundTrip(t);
        copy.Feed($"{Esc}[?1049l");
        Assert.Equal("main", copy.GetScreen()[3].Text);
    }

    [Fact]
    public void Serialize_keeps_the_title()
    {
        var t = Fed($"{Esc}]0;my title\u0007x");
        var copy = AssertRoundTrip(t);
        Assert.Equal("my title", copy.Title);
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Serialize_round_trips_every_fixture(string name)
    {
        var t = new TerminalEmulator(120, 40);
        t.Feed(FixtureBytes(name + ".raw"));
        AssertRoundTrip(t);
    }

    private static TerminalEmulator AssertRoundTrip(TerminalEmulator original)
    {
        var copy = new TerminalEmulator(original.Columns, original.Rows);
        copy.Feed(original.Serialize());
        Assert.Equal(original.DumpState(), copy.DumpState());
        return copy;
    }

    // ---- the M0 fixtures ----

    public static TheoryData<string> Fixtures() =>
    [
        "claude-rc-ready",
        "claude-rc-ready-nongit",
        "claude-rc-ready-after-prompts",
        "claude-rc-stopping",
        "prompt-trust",
        "prompt-spawn-mode",
    ];

    private static byte[] FixtureBytes(string file) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", file));

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Fixture_renders_like_the_reference_screen(string name)
    {
        var t = new TerminalEmulator(120, 40);
        t.Feed(FixtureBytes(name + ".raw"));

        // The reference .txt screens were rendered with U+FE0E removed (m0-findings 2).
        var actual = t.GetScreen().Select(r => r.Text.Replace("\uFE0E", "").TrimEnd()).ToList();
        while (actual.Count > 0 && actual[^1].Length == 0)
            actual.RemoveAt(actual.Count - 1);
        var expected = Encoding.UTF8.GetString(FixtureBytes(name + ".txt")).Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("claude-rc-ready")]
    [InlineData("claude-rc-ready-nongit")]
    [InlineData("claude-rc-ready-after-prompts")]
    [InlineData("claude-rc-stopping")]
    public void Ready_and_stopping_fixtures_are_not_prompts(string name)
    {
        var t = new TerminalEmulator(120, 40);
        t.Feed(FixtureBytes(name + ".raw"));
        Assert.Null(PromptDetector.Detect(t.GetScreen()));
    }

    [Theory]
    [InlineData("claude-rc-ready", true)]
    [InlineData("claude-rc-ready-nongit", true)]
    [InlineData("claude-rc-ready-after-prompts", true)]
    [InlineData("prompt-trust", false)]
    [InlineData("prompt-spawn-mode", false)]
    public void Ready_marker_is_seen_on_ready_screens_only(string name, bool ready)
    {
        var t = new TerminalEmulator(120, 40);
        t.Feed(FixtureBytes(name + ".raw"));
        Assert.Equal(ready, ReadyDetector.IsReady(t.GetScreen()));
    }

    [Fact]
    public void Trust_prompt_fixture_is_detected_with_the_wrapped_path_joined()
    {
        var t = new TerminalEmulator(120, 40);
        t.Feed(FixtureBytes("prompt-trust.raw"));
        var prompt = PromptDetector.Detect(t.GetScreen());
        Assert.NotNull(prompt);
        Assert.StartsWith(@"Trust C:\Users\Steven\", prompt);
        Assert.EndsWith(@"\m0\runs\gitA? [y/N]", prompt);
    }

    [Fact]
    public void Spawn_mode_prompt_fixture_is_detected()
    {
        var t = new TerminalEmulator(120, 40);
        t.Feed(FixtureBytes("prompt-spawn-mode.raw"));
        Assert.Equal("Choose [1/2] (default: 1):", PromptDetector.Detect(t.GetScreen()));
    }

    [Fact]
    public void Prompt_split_by_a_soft_wrap_is_joined()
    {
        var t = Term(cols: 10, rows: 3);
        t.Feed("Trust C:\\a\\b? [y/N]");
        Assert.Equal(@"Trust C:\a\b? [y/N]", PromptDetector.Detect(t.GetScreen()));
    }
}
