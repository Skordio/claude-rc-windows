using ClaudeRc.Core.Paths;
using ClaudeRc.Core.Prompts;

namespace ClaudeRc.Host.Sessions;

/// <summary>
/// The waiting-for-input scan of SPEC 8: every 2 s, looks at each running session's screen and
/// reports to the supervisor (<c>Supervisor.PromptChanged</c> and <c>Supervisor.ReadySeen</c>)
/// when the prompt changes or the ready output first appears.
/// </summary>
/// <remarks>
/// Callbacks run on a timer thread, outside the scanner's lock; the host forwards them to the
/// supervisor under its own lock. A session added again (a new process for the same folder)
/// starts with no prompt and not ready. Paths compare like <see cref="PathNormalizer.Comparer"/>.
/// </remarks>
public sealed class PromptScanner : IDisposable
{
    /// <summary>SPEC 8: every 2 s.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(2);

    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _entries = new(PathNormalizer.Comparer);
    private readonly Action<string> _onReady;
    private readonly Action<string, string?> _onPrompt;
    private readonly ITimer _timer;
    private bool _disposed;

    /// <summary>Starts the periodic scan.</summary>
    /// <param name="time">Clock for the timer.</param>
    /// <param name="onReady">Called once per added session when its ready output is on screen.</param>
    /// <param name="onPrompt">Called with the folder path and the new prompt text, or null when the prompt is gone.</param>
    /// <param name="interval">Scan interval; <see cref="DefaultInterval"/> by default.</param>
    public PromptScanner(TimeProvider time, Action<string> onReady, Action<string, string?> onPrompt, TimeSpan? interval = null)
    {
        ArgumentNullException.ThrowIfNull(time);
        _onReady = onReady;
        _onPrompt = onPrompt;
        var period = interval ?? DefaultInterval;
        _timer = time.CreateTimer(_ => Scan(), null, period, period);
    }

    /// <summary>Starts scanning a session's screen (replacing any earlier one for the folder).</summary>
    public void Add(string path, IScreenSource screen)
    {
        lock (_lock)
            _entries[path] = new Entry(path, screen);
    }

    /// <summary>Stops scanning the folder's session.</summary>
    public void Remove(string path)
    {
        lock (_lock)
            _entries.Remove(path);
    }

    /// <summary>One scan of every session now (the timer calls this).</summary>
    public void Scan()
    {
        Entry[] entries;
        lock (_lock)
        {
            if (_disposed)
                return;
            entries = [.. _entries.Values];
        }
        var ready = new List<string>();
        var prompts = new List<(string Path, string? Prompt)>();
        foreach (var entry in entries)
        {
            IReadOnlyList<ScreenRow> rows;
            try
            {
                rows = entry.Screen.GetScreen();
            }
#pragma warning disable CA1031 // One broken session must not stop the scan of the others.
            catch (Exception)
#pragma warning restore CA1031
            {
                continue;
            }
            var prompt = PromptDetector.Detect(rows);
            lock (_lock)
            {
                // Removed or replaced while we looked: its result no longer matters.
                if (!_entries.TryGetValue(entry.Path, out var current) || !ReferenceEquals(current, entry))
                    continue;
                if (!entry.Ready && ReadyDetector.IsReady(rows))
                {
                    entry.Ready = true;
                    ready.Add(entry.Path);
                }
                if (prompt != entry.Prompt)
                {
                    entry.Prompt = prompt;
                    prompts.Add((entry.Path, prompt));
                }
            }
        }
        foreach (var path in ready)
            _onReady(path);
        foreach (var (path, prompt) in prompts)
            _onPrompt(path, prompt);
    }

    /// <summary>Stops the timer; no callback runs after this returns, except one already running.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            _entries.Clear();
        }
        _timer.Dispose();
    }

    private sealed class Entry(string path, IScreenSource screen)
    {
        public string Path { get; } = path;
        public IScreenSource Screen { get; } = screen;
        public bool Ready { get; set; }
        public string? Prompt { get; set; }
    }
}
