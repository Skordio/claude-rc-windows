using System.Globalization;
using System.Text;
using ClaudeRc.Core.Prompts;

namespace ClaudeRc.Host.Terminal;

/// <summary>
/// A small headless terminal emulator (docs/m1-decisions.md): one per session, fed with the
/// pseudo-console's output, so the host always knows the current screen (prompt detection,
/// SPEC 8) and can replay it to a viewer (SPEC 3.3).
/// </summary>
/// <remarks>
/// <para>
/// Handles what conhost sends to a ConPTY client (m0-findings 2) and the usual xterm subset
/// beyond it: C0 controls, cursor moves (CUP, HVP, CUU/CUD/CUF/CUB, CNL/CPL, CHA, HPA/HPR,
/// VPA/VPR, CHT/CBT), erase (ED, EL, ECH), ICH/DCH/IL/DL, SU/SD, IND/NEL/RI, REP, SGR (16, 256
/// and RGB colours), DECSTBM scroll regions, DECSC/DECRC and SCOSC/SCORC, DECSTR, RIS, the
/// alternate screen (<c>?47</c>, <c>?1047</c>, <c>?1048</c>, <c>?1049</c>), autowrap
/// (<c>?7</c>) and cursor visibility (<c>?25</c>). OSC 0/2 set <see cref="Title"/>; other OSCs
/// (OSC 8 hyperlinks), DCS, SOS, PM and APC strings are skipped; other modes (<c>?1004</c>,
/// <c>?9001</c>, <c>?2004</c>, mouse modes, …) are accepted and ignored. Not supported: origin
/// mode, insert mode, custom tab stops, character sets other than ASCII/UTF-8, and reflow on
/// resize (conhost repaints the whole screen after a resize anyway).
/// </para>
/// <para>Not thread-safe: the owner serialises calls.</para>
/// </remarks>
public sealed class TerminalEmulator
{
    /// <summary>Scrollback kept by default (SPEC 3.3: the last 5,000 lines).</summary>
    public const int DefaultScrollback = 5000;

    private const int MaxParams = 32;
    private const int MaxOscLength = 4096;
    private const int MaxCellText = 32;

    private static readonly string[] AsciiText = [.. Enumerable.Range(0, 128).Select(c => ((char)c).ToString())];

    private readonly Decoder _decoder = new UTF8Encoding(false, false).GetDecoder();
    private readonly int _scrollbackLimit;
    private readonly Ring<Line> _scrollback;
    private char[] _chars = new char[4096];
    private char _pendingHigh;

    private Line[] _main;
    private Line[] _alt;
    private bool _altActive;
    private int _x, _y;
    private bool _wrapNext;
    private Style _pen = Style.Default;
    private int _top, _bottom;
    private bool _autowrap = true;
    private SavedCursor _saved;
    private SavedCursor _savedForAlt;
    private int _lastPrinted = -1;

    private State _state;
    private readonly int[] _params = new int[MaxParams];
    private int _paramCount;
    private bool _paramStarted;
    private char _prefix;
    private char _intermediate;
    private readonly StringBuilder _osc = new();

    /// <summary>Creates an empty screen.</summary>
    /// <param name="columns">Width in cells (at least 1).</param>
    /// <param name="rows">Height in rows (at least 1).</param>
    /// <param name="scrollbackLimit">Lines kept above the screen.</param>
    public TerminalEmulator(int columns, int rows, int scrollbackLimit = DefaultScrollback)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(scrollbackLimit);
        Columns = columns;
        Rows = rows;
        _scrollbackLimit = scrollbackLimit;
        _scrollback = new Ring<Line>(scrollbackLimit);
        _main = NewScreen(columns, rows);
        _alt = NewScreen(columns, rows);
        _bottom = rows - 1;
    }

    /// <summary>Width in cells.</summary>
    public int Columns { get; private set; }

    /// <summary>Height in rows.</summary>
    public int Rows { get; private set; }

    /// <summary>Cursor row, 0-based.</summary>
    public int CursorRow => _y;

    /// <summary>Cursor column, 0-based.</summary>
    public int CursorColumn => _x;

    /// <summary>False after <c>CSI ?25l</c>.</summary>
    public bool CursorVisible { get; private set; } = true;

    /// <summary>True while the alternate screen is shown.</summary>
    public bool AlternateScreen => _altActive;

    /// <summary>The window title from the last OSC 0 or OSC 2.</summary>
    public string? Title { get; private set; }

    private Line[] Screen => _altActive ? _alt : _main;

    // ------------------------------------------------------------------ input

    /// <summary>Feeds raw output bytes (UTF-8; a sequence split across calls is fine).</summary>
    public void Feed(ReadOnlySpan<byte> utf8)
    {
        var needed = _decoder.GetCharCount(utf8, flush: false);
        if (needed > _chars.Length)
            _chars = new char[Math.Max(needed, _chars.Length * 2)];
        var n = _decoder.GetChars(utf8, _chars, flush: false);
        FeedChars(_chars.AsSpan(0, n));
    }

    /// <summary>Feeds decoded text.</summary>
    public void Feed(string text) => FeedChars(text);

    private void FeedChars(ReadOnlySpan<char> chars)
    {
        foreach (var c in chars)
        {
            if (_pendingHigh != 0)
            {
                var high = _pendingHigh;
                _pendingHigh = '\0';
                if (char.IsLowSurrogate(c))
                {
                    Process(char.ConvertToUtf32(high, c));
                    continue;
                }
                Process(0xFFFD);
            }
            if (char.IsHighSurrogate(c))
                _pendingHigh = c;
            else if (char.IsLowSurrogate(c))
                Process(0xFFFD);
            else
                Process(c);
        }
    }

    private enum State { Ground, Escape, EscapeIntermediate, Csi, Osc, OscEscape, String, StringEscape }

    private void Process(int c)
    {
        switch (_state)
        {
            case State.Ground:
                if (c < 0x20 || c == 0x7F)
                    Control(c);
                else if (c is >= 0x80 and < 0xA0)
                {
                    // C1 controls: not used in UTF-8 output; drop.
                }
                else
                    Print(c);
                return;

            case State.Escape:
                if (c < 0x20)
                {
                    if (c is 0x18 or 0x1A)
                        _state = State.Ground;
                    else if (c == 0x1B)
                        _state = State.Escape;
                    else
                        Control(c);
                    return;
                }
                Escape(c);
                return;

            case State.EscapeIntermediate:
                // Charset designation (ESC ( B, …), DECALN (ESC # 8): the final byte is consumed.
                if (c >= 0x30 || c < 0x20)
                    _state = State.Ground;
                return;

            case State.Csi:
                CsiByte(c);
                return;

            case State.Osc:
                if (c == 0x07)
                    EndOsc();
                else if (c == 0x1B)
                    _state = State.OscEscape;
                else if (c is 0x18 or 0x1A)
                    _state = State.Ground;
                else if (_osc.Length < MaxOscLength)
                    _osc.Append(char.ConvertFromUtf32(c));
                return;

            case State.OscEscape:
                if (c == '\\')
                    EndOsc();
                else
                {
                    // Not ST: the OSC is abandoned and this is a new escape sequence.
                    _state = State.Escape;
                    Process(c);
                }
                return;

            case State.String:
                if (c == 0x1B)
                    _state = State.StringEscape;
                else if (c is 0x18 or 0x1A)
                    _state = State.Ground;
                return;

            case State.StringEscape:
                if (c == '\\')
                    _state = State.Ground;
                else
                {
                    _state = State.Escape;
                    Process(c);
                }
                return;
        }
    }

    private void Control(int c)
    {
        switch (c)
        {
            case 0x08: // BS
                if (_x > 0)
                    _x--;
                _wrapNext = false;
                break;
            case 0x09: // HT
                _x = Math.Min(Columns - 1, (_x / 8 + 1) * 8);
                _wrapNext = false;
                break;
            case 0x0A or 0x0B or 0x0C: // LF, VT, FF
                LineFeed();
                break;
            case 0x0D: // CR
                _x = 0;
                _wrapNext = false;
                break;
            case 0x1B:
                _state = State.Escape;
                break;
            default: // BEL, NUL, SO/SI, DEL, …
                break;
        }
    }

    private void Escape(int c)
    {
        _state = State.Ground;
        switch (c)
        {
            case '[':
                _paramCount = 0;
                _paramStarted = false;
                _prefix = '\0';
                _intermediate = '\0';
                Array.Clear(_params);
                _state = State.Csi;
                break;
            case ']':
                _osc.Clear();
                _state = State.Osc;
                break;
            case 'P' or 'X' or '^' or '_':
                _state = State.String;
                break;
            case '7': SaveCursor(ref _saved); break;
            case '8': RestoreCursor(_saved); break;
            case 'D': LineFeed(); break;
            case 'E': _x = 0; LineFeed(); break;
            case 'M': ReverseIndex(); break;
            case 'c': Reset(); break;
            case >= 0x20 and <= 0x2F: _state = State.EscapeIntermediate; break;
            default: break; // ESC = / ESC > (keypad), ESC H (tab set), ESC \ (stray ST), …
        }
    }

    // ------------------------------------------------------------------ CSI

    private void CsiByte(int c)
    {
        if (c is >= '0' and <= '9')
        {
            if (_paramCount == 0)
                _paramCount = 1;
            ref var p = ref _params[_paramCount - 1];
            p = Math.Min(p * 10 + (c - '0'), 99999);
            _paramStarted = true;
            return;
        }
        if (c is ';' or ':')
        {
            if (_paramCount == 0)
                _paramCount = 1;
            if (_paramCount < MaxParams)
                _paramCount++;
            _paramStarted = true;
            return;
        }
        if (c is '?' or '>' or '<' or '=')
        {
            if (!_paramStarted)
                _prefix = (char)c;
            return;
        }
        if (c is >= 0x20 and <= 0x2F)
        {
            _intermediate = (char)c;
            return;
        }
        if (c is >= 0x40 and <= 0x7E)
        {
            _state = State.Ground;
            Csi((char)c);
            return;
        }
        if (c == 0x1B)
        {
            _state = State.Escape;
            return;
        }
        if (c is 0x18 or 0x1A)
        {
            _state = State.Ground;
            return;
        }
        if (c < 0x20)
            Control(c);
        // Anything else inside a CSI is ignored.
    }

    private int Param(int index, int fallback)
    {
        var v = index < _paramCount ? _params[index] : 0;
        return v == 0 ? fallback : v;
    }

    private void Csi(char final)
    {
        if (_prefix == '?')
        {
            if (final is 'h' or 'l')
                for (var i = 0; i < Math.Max(1, _paramCount); i++)
                    PrivateMode(_params[i], final == 'h');
            return;
        }
        if (_prefix != '\0')
            return; // CSI > …, CSI = …: device queries and key modifiers.
        if (_intermediate != '\0')
        {
            if (_intermediate == '!' && final == 'p')
                SoftReset();
            return; // DECSCUSR (CSI SP q) and the like.
        }

        var n = Param(0, 1);
        switch (final)
        {
            case '@': InsertChars(n); break;
            case 'A': MoveTo(_y < _top ? Math.Max(0, _y - n) : Math.Max(_top, _y - n), _x); break;
            case 'B' or 'e': MoveTo(_y > _bottom ? Math.Min(Rows - 1, _y + n) : Math.Min(_bottom, _y + n), _x); break;
            case 'C' or 'a': MoveTo(_y, _x + n); break;
            case 'D': MoveTo(_y, _x - n); break;
            case 'E': MoveTo(_y > _bottom ? Math.Min(Rows - 1, _y + n) : Math.Min(_bottom, _y + n), 0); break;
            case 'F': MoveTo(_y < _top ? Math.Max(0, _y - n) : Math.Max(_top, _y - n), 0); break;
            case 'G' or '`': MoveTo(_y, n - 1); break;
            case 'H' or 'f': MoveTo(Param(0, 1) - 1, Param(1, 1) - 1); break;
            case 'I': for (var i = 0; i < n; i++) MoveTo(_y, (_x / 8 + 1) * 8); break;
            case 'Z': for (var i = 0; i < n; i++) MoveTo(_y, (_x - 1) / 8 * 8); break;
            case 'J': EraseDisplay(Param(0, 0)); break;
            case 'K': EraseLine(Param(0, 0)); break;
            case 'L': InsertLines(n); break;
            case 'M': DeleteLines(n); break;
            case 'P': DeleteChars(n); break;
            case 'S': ScrollUp(_top, _bottom, n); break;
            case 'T': if (_paramCount <= 1) ScrollDown(_top, _bottom, n); break;
            case 'X': EraseCells(Screen[_y], _x, Math.Min(Columns, _x + n)); _wrapNext = false; break;
            case 'b': if (_lastPrinted >= 0) for (var i = 0; i < Math.Min(n, Columns * Rows); i++) Print(_lastPrinted); break;
            case 'd': MoveTo(n - 1, _x); break;
            case 'm': Sgr(); break;
            case 'r': SetScrollRegion(Param(0, 1) - 1, Param(1, Rows) - 1); break;
            case 's': SaveCursor(ref _saved); break;
            case 'u': RestoreCursor(_saved); break;
            default: break; // SM/RM (IRM, LNM), DSR, window ops, …
        }
    }

    private void PrivateMode(int mode, bool set)
    {
        switch (mode)
        {
            case 7:
                _autowrap = set;
                if (!set)
                    _wrapNext = false;
                break;
            case 25:
                CursorVisible = set;
                break;
            case 47 or 1047:
                SwitchScreen(set, saveCursor: false);
                break;
            case 1048:
                if (set)
                    SaveCursor(ref _saved);
                else
                    RestoreCursor(_saved);
                break;
            case 1049:
                SwitchScreen(set, saveCursor: true);
                break;
            default:
                break; // ?1004 focus events, ?9001 win32-input-mode, ?2004 paste, mouse modes, …
        }
    }

    private void SwitchScreen(bool alt, bool saveCursor)
    {
        if (alt == _altActive)
            return;
        if (alt)
        {
            if (saveCursor)
                SaveCursor(ref _savedForAlt);
            _altActive = true;
            _alt = NewScreen(Columns, Rows);
        }
        else
        {
            _altActive = false;
            if (saveCursor)
                RestoreCursor(_savedForAlt);
        }
        _top = 0;
        _bottom = Rows - 1;
        _wrapNext = false;
    }

    // ------------------------------------------------------------------ SGR

    private void Sgr()
    {
        var count = Math.Max(1, _paramCount);
        for (var i = 0; i < count; i++)
        {
            var p = _params[i];
            switch (p)
            {
                case 0: _pen = Style.Default; break;
                case 1: _pen = _pen.With(StyleFlags.Bold, true); break;
                case 2: _pen = _pen.With(StyleFlags.Dim, true); break;
                case 3: _pen = _pen.With(StyleFlags.Italic, true); break;
                case 4 or 21: _pen = _pen.With(StyleFlags.Underline, true); break;
                case 5 or 6: _pen = _pen.With(StyleFlags.Blink, true); break;
                case 7: _pen = _pen.With(StyleFlags.Inverse, true); break;
                case 8: _pen = _pen.With(StyleFlags.Hidden, true); break;
                case 9: _pen = _pen.With(StyleFlags.Strike, true); break;
                case 22: _pen = _pen.With(StyleFlags.Bold | StyleFlags.Dim, false); break;
                case 23: _pen = _pen.With(StyleFlags.Italic, false); break;
                case 24: _pen = _pen.With(StyleFlags.Underline, false); break;
                case 25: _pen = _pen.With(StyleFlags.Blink, false); break;
                case 27: _pen = _pen.With(StyleFlags.Inverse, false); break;
                case 28: _pen = _pen.With(StyleFlags.Hidden, false); break;
                case 29: _pen = _pen.With(StyleFlags.Strike, false); break;
                case >= 30 and <= 37: _pen = _pen with { Fg = p - 30 }; break;
                case 38: _pen = _pen with { Fg = ExtendedColor(ref i, count, _pen.Fg) }; break;
                case 39: _pen = _pen with { Fg = Style.DefaultColor }; break;
                case >= 40 and <= 47: _pen = _pen with { Bg = p - 40 }; break;
                case 48: _pen = _pen with { Bg = ExtendedColor(ref i, count, _pen.Bg) }; break;
                case 49: _pen = _pen with { Bg = Style.DefaultColor }; break;
                case 53: _pen = _pen.With(StyleFlags.Overline, true); break;
                case 55: _pen = _pen.With(StyleFlags.Overline, false); break;
                case >= 90 and <= 97: _pen = _pen with { Fg = p - 90 + 8 }; break;
                case >= 100 and <= 107: _pen = _pen with { Bg = p - 100 + 8 }; break;
                default: break;
            }
        }
    }

    private int ExtendedColor(ref int i, int count, int current)
    {
        if (i + 1 >= count)
            return current;
        switch (_params[i + 1])
        {
            case 5 when i + 2 < count:
                i += 2;
                return Math.Clamp(_params[i], 0, 255);
            case 2 when i + 4 < count:
                var r = Math.Clamp(_params[i + 2], 0, 255);
                var g = Math.Clamp(_params[i + 3], 0, 255);
                var b = Math.Clamp(_params[i + 4], 0, 255);
                i += 4;
                return Style.Rgb(r, g, b);
            default:
                i = count;
                return current;
        }
    }

    // ------------------------------------------------------------------ OSC

    private void EndOsc()
    {
        _state = State.Ground;
        var text = _osc.ToString();
        var semi = text.IndexOf(';', StringComparison.Ordinal);
        if (semi < 0)
            return;
        if (text.AsSpan(0, semi) is "0" or "2")
            Title = text[(semi + 1)..];
        // OSC 8 (hyperlinks) and everything else: ignored.
    }

    // ------------------------------------------------------------------ printing

    private void Print(int c)
    {
        var width = CharWidth.Of(c);
        if (width == 0)
        {
            AttachToPrevious(c);
            return;
        }
        if (width == 2 && Columns < 2)
            width = 1;

        if (_wrapNext)
        {
            Screen[_y].Wrapped = true;
            _x = 0;
            LineFeed();
        }
        if (width == 2 && _x == Columns - 1)
        {
            if (!_autowrap)
                return;
            var row = Screen[_y];
            ClearWideAt(row, _x);
            row.Cells[_x] = Cell.Blank(_pen);
            row.Wrapped = true;
            _x = 0;
            LineFeed();
        }

        var line = Screen[_y];
        ClearWideAt(line, _x);
        if (width == 2)
            ClearWideAt(line, _x + 1);
        line.Cells[_x] = new Cell(c == ' ' ? null : TextOf(c), (byte)width, _pen);
        if (width == 2)
            line.Cells[_x + 1] = new Cell(null, 0, _pen);
        _lastPrinted = c;

        _x += width;
        if (_x >= Columns)
        {
            _x = Columns - 1;
            _wrapNext = _autowrap;
        }
    }

    private static string TextOf(int c) => c < 128 ? AsciiText[c] : char.ConvertFromUtf32(c);

    private void AttachToPrevious(int c)
    {
        var x = _wrapNext ? _x : _x - 1;
        if (x < 0)
            return;
        var line = Screen[_y];
        if (line.Cells[x].Width == 0 && x > 0)
            x--;
        ref var cell = ref line.Cells[x];
        var text = cell.Text ?? " ";
        if (text.Length + 2 <= MaxCellText)
            cell = cell with { Text = text + char.ConvertFromUtf32(c) };
    }

    /// <summary>Before writing at <paramref name="x"/>, blanks the other half of a wide character there.</summary>
    private void ClearWideAt(Line line, int x)
    {
        if (x < 0 || x >= line.Cells.Length)
            return;
        var cell = line.Cells[x];
        if (cell.Width == 0 && x > 0 && line.Cells[x - 1].Width == 2)
            line.Cells[x - 1] = Cell.Blank(line.Cells[x - 1].Style);
        else if (cell.Width == 2 && x + 1 < line.Cells.Length)
            line.Cells[x + 1] = Cell.Blank(cell.Style);
    }

    // ------------------------------------------------------------------ movement and scrolling

    private void MoveTo(int row, int col)
    {
        _y = Math.Clamp(row, 0, Rows - 1);
        _x = Math.Clamp(col, 0, Columns - 1);
        _wrapNext = false;
    }

    private void LineFeed()
    {
        _wrapNext = false;
        if (_y == _bottom)
            ScrollUp(_top, _bottom, 1);
        else if (_y < Rows - 1)
            _y++;
    }

    private void ReverseIndex()
    {
        _wrapNext = false;
        if (_y == _top)
            ScrollDown(_top, _bottom, 1);
        else if (_y > 0)
            _y--;
    }

    private void ScrollUp(int top, int bottom, int n)
    {
        var screen = Screen;
        n = Math.Min(n, bottom - top + 1);
        for (var k = 0; k < n; k++)
        {
            var gone = screen[top];
            if (!_altActive && top == 0 && _scrollbackLimit > 0)
                _scrollback.Add(gone);
            Array.Copy(screen, top + 1, screen, top, bottom - top);
            screen[bottom] = NewLine(Columns, _pen);
        }
    }

    private void ScrollDown(int top, int bottom, int n)
    {
        var screen = Screen;
        n = Math.Min(n, bottom - top + 1);
        for (var k = 0; k < n; k++)
        {
            Array.Copy(screen, top, screen, top + 1, bottom - top);
            screen[top] = NewLine(Columns, _pen);
        }
    }

    private void SetScrollRegion(int top, int bottom)
    {
        bottom = Math.Min(bottom, Rows - 1);
        if (top < 0 || top >= bottom)
            return;
        _top = top;
        _bottom = bottom;
        MoveTo(0, 0);
    }

    private void InsertLines(int n)
    {
        if (_y < _top || _y > _bottom)
            return;
        ScrollDown(_y, _bottom, n);
        _x = 0;
        _wrapNext = false;
    }

    private void DeleteLines(int n)
    {
        if (_y < _top || _y > _bottom)
            return;
        // Lines deleted inside the screen never go to the scrollback.
        var screen = Screen;
        n = Math.Min(n, _bottom - _y + 1);
        for (var k = 0; k < n; k++)
        {
            Array.Copy(screen, _y + 1, screen, _y, _bottom - _y);
            screen[_bottom] = NewLine(Columns, _pen);
        }
        _x = 0;
        _wrapNext = false;
    }

    // ------------------------------------------------------------------ editing

    private void EraseCells(Line line, int from, int to)
    {
        if (from >= to)
            return;
        ClearWideAt(line, from);
        ClearWideAt(line, to - 1);
        for (var i = from; i < to; i++)
            line.Cells[i] = Cell.Blank(_pen);
    }

    private void EraseLine(int mode)
    {
        var line = Screen[_y];
        switch (mode)
        {
            case 0:
                EraseCells(line, _x, Columns);
                line.Wrapped = false;
                break;
            case 1:
                EraseCells(line, 0, _x + 1);
                break;
            case 2:
                EraseCells(line, 0, Columns);
                line.Wrapped = false;
                break;
        }
        _wrapNext = false;
    }

    private void EraseDisplay(int mode)
    {
        var screen = Screen;
        switch (mode)
        {
            case 0:
                EraseLine(0);
                for (var r = _y + 1; r < Rows; r++)
                    screen[r] = NewLine(Columns, _pen);
                break;
            case 1:
                EraseLine(1);
                for (var r = 0; r < _y; r++)
                    screen[r] = NewLine(Columns, _pen);
                break;
            case 2:
                for (var r = 0; r < Rows; r++)
                    screen[r] = NewLine(Columns, _pen);
                break;
            case 3:
                _scrollback.Clear();
                break;
        }
        _wrapNext = false;
    }

    private void InsertChars(int n)
    {
        var line = Screen[_y];
        n = Math.Min(n, Columns - _x);
        ClearWideAt(line, _x);
        Array.Copy(line.Cells, _x, line.Cells, _x + n, Columns - _x - n);
        for (var i = _x; i < _x + n; i++)
            line.Cells[i] = Cell.Blank(_pen);
        FixCutWide(line);
        _wrapNext = false;
    }

    private void DeleteChars(int n)
    {
        var line = Screen[_y];
        n = Math.Min(n, Columns - _x);
        ClearWideAt(line, _x);
        ClearWideAt(line, _x + n - 1);
        Array.Copy(line.Cells, _x + n, line.Cells, _x, Columns - _x - n);
        for (var i = Columns - n; i < Columns; i++)
            line.Cells[i] = Cell.Blank(_pen);
        FixCutWide(line);
        _wrapNext = false;
    }

    /// <summary>A wide character shifted onto the last column has lost its right half: blank it.</summary>
    private static void FixCutWide(Line line)
    {
        var last = line.Cells.Length - 1;
        if (line.Cells[last].Width == 2)
            line.Cells[last] = Cell.Blank(line.Cells[last].Style);
        if (line.Cells[0].Width == 0)
            line.Cells[0] = Cell.Blank(line.Cells[0].Style);
    }

    // ------------------------------------------------------------------ cursor save, resets

    private struct SavedCursor
    {
        public int X, Y;
        public Style Pen;
        public bool WrapNext;
    }

    private void SaveCursor(ref SavedCursor slot) =>
        slot = new SavedCursor { X = _x, Y = _y, Pen = _pen, WrapNext = _wrapNext };

    private void RestoreCursor(SavedCursor slot)
    {
        _x = Math.Clamp(slot.X, 0, Columns - 1);
        _y = Math.Clamp(slot.Y, 0, Rows - 1);
        _pen = slot.Pen;
        _wrapNext = slot.WrapNext && _autowrap && _x == Columns - 1;
    }

    private void SoftReset()
    {
        _pen = Style.Default;
        CursorVisible = true;
        _autowrap = true;
        _top = 0;
        _bottom = Rows - 1;
        _saved = default;
        _wrapNext = false;
    }

    private void Reset()
    {
        _main = NewScreen(Columns, Rows);
        _alt = NewScreen(Columns, Rows);
        _altActive = false;
        _scrollback.Clear();
        SoftReset();
        _savedForAlt = default;
        _x = _y = 0;
        _lastPrinted = -1;
        Title = null;
    }

    // ------------------------------------------------------------------ resize

    /// <summary>
    /// Changes the size. Rows are cut or padded, not reflowed. When the screen gets shorter,
    /// blank rows below the cursor go first, then rows from the top move to the scrollback.
    /// </summary>
    public void Resize(int columns, int rows)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);
        if (columns == Columns && rows == Rows)
            return;

        if (_altActive)
        {
            _main = ResizeScreen(_main, columns, rows, ref _savedForAlt.Y, toScrollback: true);
            _alt = ResizeScreen(_alt, columns, rows, ref _y, toScrollback: false);
        }
        else
        {
            _main = ResizeScreen(_main, columns, rows, ref _y, toScrollback: true);
            var unused = 0;
            _alt = ResizeScreen(_alt, columns, rows, ref unused, toScrollback: false);
        }
        Columns = columns;
        Rows = rows;
        _x = Math.Min(_x, columns - 1);
        _y = Math.Clamp(_y, 0, rows - 1);
        _top = 0;
        _bottom = rows - 1;
        _wrapNext = false;
        _saved.X = Math.Min(_saved.X, columns - 1);
        _saved.Y = Math.Min(_saved.Y, rows - 1);
        _savedForAlt.X = Math.Min(_savedForAlt.X, columns - 1);
        _savedForAlt.Y = Math.Min(_savedForAlt.Y, rows - 1);
    }

    private Line[] ResizeScreen(Line[] screen, int columns, int rows, ref int cursorY, bool toScrollback)
    {
        var lines = new List<Line>(screen);
        // Shorter: drop blank rows below the cursor, then move rows off the top.
        for (var r = lines.Count - 1; lines.Count > rows && r > cursorY; r--)
        {
            if (lines[r].IsBlank)
                lines.RemoveAt(r);
        }
        while (lines.Count > rows)
        {
            if (toScrollback && _scrollbackLimit > 0)
                _scrollback.Add(lines[0]);
            lines.RemoveAt(0);
            cursorY--;
        }
        while (lines.Count < rows)
            lines.Add(NewLine(columns, Style.Default));
        foreach (var line in lines)
            line.Resize(columns);
        cursorY = Math.Max(0, cursorY);
        return [.. lines];
    }

    // ------------------------------------------------------------------ queries

    /// <summary>
    /// The current screen (the alternate one while it is shown), top to bottom. A row's text
    /// keeps its trailing blanks only when it soft-wraps into the next row.
    /// </summary>
    public IReadOnlyList<ScreenRow> GetScreen() => [.. Screen.Select(ToRow)];

    /// <summary>The scrollback of the main screen, oldest first.</summary>
    public IReadOnlyList<ScreenRow> GetScrollback()
    {
        var rows = new ScreenRow[_scrollback.Count];
        for (var i = 0; i < rows.Length; i++)
            rows[i] = ToRow(_scrollback[i]);
        return rows;
    }

    private static ScreenRow ToRow(Line line)
    {
        var sb = new StringBuilder(line.Cells.Length);
        foreach (var cell in line.Cells)
        {
            if (cell.Width != 0)
                sb.Append(cell.Text ?? " ");
        }
        var text = sb.ToString();
        return new ScreenRow(line.Wrapped ? text : text.TrimEnd(' '), line.Wrapped);
    }

    // ------------------------------------------------------------------ replay

    /// <summary>
    /// VT bytes (UTF-8) that rebuild this terminal in a fresh one of the same size: scrollback
    /// and screen with styles and soft wraps, the alternate screen, scroll region, title,
    /// cursor position (a pending wrap included), pen, autowrap and cursor visibility. The
    /// viewer replays them before the live stream (SPEC 3.3).
    /// </summary>
    public byte[] Serialize()
    {
        var sb = new StringBuilder();
        sb.Append("\u001bc");
        if (Title is not null)
            sb.Append("\u001b]0;").Append(StripControls(Title)).Append('\u0007');

        var main = new List<Line>(_scrollback.Count + Rows);
        for (var i = 0; i < _scrollback.Count; i++)
            main.Add(_scrollback[i]);
        main.AddRange(_main);
        AppendLines(sb, main);

        if (_altActive)
        {
            AppendCursorPosition(sb, _savedForAlt.Y, _savedForAlt.X);
            sb.Append("\u001b[?1049h");
            AppendLines(sb, _alt);
        }
        if (_top != 0 || _bottom != Rows - 1)
            sb.Append(CultureInfo.InvariantCulture, $"\u001b[{_top + 1};{_bottom + 1}r");

        var screen = Screen;
        if (_wrapNext)
        {
            // Rewrite the last cell so the copy ends up with the same pending wrap.
            var row = screen[_y];
            var x = Columns - 1;
            if (row.Cells[x].Width == 0 && x > 0)
                x--;
            AppendCursorPosition(sb, _y, x);
            var cur = Style.Default;
            sb.Append("\u001b[0m");
            AppendCell(sb, row.Cells[x], ref cur);
        }
        else
        {
            AppendCursorPosition(sb, _y, _x);
        }
        sb.Append(_pen.ToSgr());
        if (!_autowrap)
            sb.Append("\u001b[?7l");
        if (!CursorVisible)
            sb.Append("\u001b[?25l");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private void AppendLines(StringBuilder sb, IReadOnlyList<Line> lines)
    {
        var cur = Style.Default;
        var previousWrapped = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var last = i == lines.Count - 1;
            var wraps = line.Wrapped && !last;
            // A wrapping row is written in full so the copy wraps at the same place; any other
            // row only up to its last visible cell (the fresh terminal is blank).
            var end = wraps ? Columns : line.ContentLength(Columns);
            if (previousWrapped && end == 0)
                end = 1; // print something so the previous row's wrap happens
            for (var x = 0; x < end; x++)
            {
                var cell = x < line.Cells.Length ? line.Cells[x] : Cell.Blank(Style.Default);
                if (cell.Width == 0)
                    continue;
                if (cell.Width == 2 && x == Columns - 1)
                    cell = Cell.Blank(cell.Style);
                AppendCell(sb, cell, ref cur);
            }
            if (!last && !wraps)
            {
                if (cur != Style.Default)
                {
                    sb.Append("\u001b[0m");
                    cur = Style.Default;
                }
                sb.Append("\r\n");
            }
            previousWrapped = wraps;
        }
        if (cur != Style.Default)
            sb.Append("\u001b[0m");
    }

    private static void AppendCell(StringBuilder sb, Cell cell, ref Style cur)
    {
        if (cell.Style != cur)
        {
            sb.Append(cell.Style.ToSgr());
            cur = cell.Style;
        }
        sb.Append(cell.Text ?? " ");
    }

    private static void AppendCursorPosition(StringBuilder sb, int row, int col) =>
        sb.Append(CultureInfo.InvariantCulture, $"\u001b[{row + 1};{col + 1}H");

    private static string StripControls(string s) => new([.. s.Where(c => c >= 0x20 && c != 0x7F)]);

    /// <summary>Everything the replay must reproduce, as text, for tests.</summary>
    internal string DumpState()
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture,
            $"size={Columns}x{Rows} cursor={_y},{_x} wrapNext={_wrapNext} visible={CursorVisible} autowrap={_autowrap} " +
            $"pen={_pen} region={_top}-{_bottom} alt={_altActive} title={Title}\n");
        if (_altActive)
            sb.Append(CultureInfo.InvariantCulture, $"altSaved={_savedForAlt.Y},{_savedForAlt.X}\n");
        void Dump(string label, IEnumerable<Line> lines)
        {
            foreach (var line in lines)
            {
                sb.Append(label).Append(line.Wrapped ? " W|" : " -|");
                foreach (var cell in line.Cells)
                    sb.Append(cell.Text ?? " ").Append('\u0001').Append(cell.Width).Append(cell.Style).Append('|');
                sb.Append('\n');
            }
        }
        var back = new List<Line>();
        for (var i = 0; i < _scrollback.Count; i++)
            back.Add(_scrollback[i]);
        Dump("back", back);
        Dump("main", _main);
        if (_altActive)
            Dump("alt", _alt);
        return sb.ToString();
    }

    // ------------------------------------------------------------------ model

    private static Line[] NewScreen(int columns, int rows)
    {
        var screen = new Line[rows];
        for (var r = 0; r < rows; r++)
            screen[r] = NewLine(columns, Style.Default);
        return screen;
    }

    private static Line NewLine(int columns, Style pen)
    {
        var line = new Line(columns);
        if (pen.Bg != Style.DefaultColor)
        {
            var blank = Cell.Blank(pen);
            Array.Fill(line.Cells, blank);
        }
        return line;
    }

    private sealed class Line
    {
        public Line(int columns)
        {
            Cells = new Cell[columns];
            Array.Fill(Cells, Cell.Blank(Style.Default));
        }

        public Cell[] Cells { get; private set; }

        /// <summary>The row continues on the next one (soft wrap).</summary>
        public bool Wrapped { get; set; }

        public bool IsBlank => !Wrapped && ContentLength(Cells.Length) == 0;

        /// <summary>Cells up to and including the last one that is not a default blank.</summary>
        public int ContentLength(int limit)
        {
            for (var i = Math.Min(limit, Cells.Length) - 1; i >= 0; i--)
            {
                if (!Cells[i].IsDefaultBlank)
                    return i + 1;
            }
            return 0;
        }

        public void Resize(int columns)
        {
            if (columns == Cells.Length)
                return;
            var old = Cells;
            Cells = new Cell[columns];
            Array.Fill(Cells, Cell.Blank(Style.Default));
            Array.Copy(old, Cells, Math.Min(old.Length, columns));
            FixCutWide(this);
        }
    }

    /// <summary>One cell. Width 1 or 2 for a character (or blank), 0 for the right half of a wide one.</summary>
    private readonly record struct Cell(string? Text, byte Width, Style Style)
    {
        public static Cell Blank(Style pen) => new(null, 1, new Style(Style.DefaultColor, pen.Bg, StyleFlags.None));

        public bool IsDefaultBlank => Text is null && Width == 1 && Style == Style.Default;
    }

    /// <summary>A fixed-size ring that drops its oldest item when full.</summary>
    private sealed class Ring<T>(int capacity)
    {
        private readonly T[] _items = new T[Math.Max(capacity, 1)];
        private int _start;

        public int Count { get; private set; }

        public T this[int index] => _items[(_start + index) % _items.Length];

        public void Add(T item)
        {
            if (capacity == 0)
                return;
            if (Count < _items.Length)
            {
                _items[(_start + Count) % _items.Length] = item;
                Count++;
            }
            else
            {
                _items[_start] = item;
                _start = (_start + 1) % _items.Length;
            }
        }

        public void Clear()
        {
            Array.Clear(_items);
            _start = 0;
            Count = 0;
        }
    }
}

/// <summary>Text attributes of a cell.</summary>
[Flags]
internal enum StyleFlags : ushort
{
    None = 0,
    Bold = 1,
    Dim = 2,
    Italic = 4,
    Underline = 8,
    Blink = 16,
    Inverse = 32,
    Hidden = 64,
    Strike = 128,
    Overline = 256,
}

/// <summary>
/// Pen and cell style. Colours: <see cref="DefaultColor"/>, 0–255 (palette), or
/// <see cref="Rgb"/> values.
/// </summary>
internal readonly record struct Style(int Fg, int Bg, StyleFlags Flags)
{
    public const int DefaultColor = -1;
    private const int RgbFlag = 0x1000000;

    public static readonly Style Default = new(DefaultColor, DefaultColor, StyleFlags.None);

    public static int Rgb(int r, int g, int b) => RgbFlag | (r << 16) | (g << 8) | b;

    public Style With(StyleFlags flags, bool on) => this with { Flags = on ? Flags | flags : Flags & ~flags };

    /// <summary>A full SGR sequence that sets exactly this style, starting from a reset.</summary>
    public string ToSgr()
    {
        var sb = new StringBuilder("\u001b[0");
        var flags = Flags;
        void Flag(StyleFlags f, string code)
        {
            if ((flags & f) != 0)
                sb.Append(';').Append(code);
        }
        Flag(StyleFlags.Bold, "1");
        Flag(StyleFlags.Dim, "2");
        Flag(StyleFlags.Italic, "3");
        Flag(StyleFlags.Underline, "4");
        Flag(StyleFlags.Blink, "5");
        Flag(StyleFlags.Inverse, "7");
        Flag(StyleFlags.Hidden, "8");
        Flag(StyleFlags.Strike, "9");
        Flag(StyleFlags.Overline, "53");
        AppendColor(sb, Fg, 30, 90, 38);
        AppendColor(sb, Bg, 40, 100, 48);
        return sb.Append('m').ToString();
    }

    private static void AppendColor(StringBuilder sb, int color, int basic, int bright, int extended)
    {
        if (color == DefaultColor)
            return;
        if ((color & RgbFlag) != 0)
            sb.Append(CultureInfo.InvariantCulture, $";{extended};2;{(color >> 16) & 255};{(color >> 8) & 255};{color & 255}");
        else if (color < 8)
            sb.Append(';').Append(basic + color);
        else if (color < 16)
            sb.Append(';').Append(bright + color - 8);
        else
            sb.Append(CultureInfo.InvariantCulture, $";{extended};5;{color}");
    }
}
