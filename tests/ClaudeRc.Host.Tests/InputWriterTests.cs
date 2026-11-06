using System.Diagnostics;
using ClaudeRc.Host.Sessions;

namespace ClaudeRc.Host.Tests;

/// <summary>
/// Input to the pseudo-console goes through a queue and a writer thread, so a console that stops
/// reading never blocks the caller (the host's lock, the stop sequence of SPEC 6.4).
/// </summary>
public sealed class InputWriterTests
{
    private static readonly TimeSpan Quick = TimeSpan.FromMilliseconds(500);

    [Fact]
    public void Writes_reach_the_stream_in_order()
    {
        var stream = new GateStream(open: true);
        using var writer = new InputWriter(stream, "test");
        Assert.True(writer.TryWrite("ab"u8));
        Assert.True(writer.TryWrite("c"u8));
        Assert.True(writer.TryWrite([0x03]));
        FakeClaudeHarness.WaitUntil(() => stream.Written.Length == 4, "all bytes written");
        Assert.Equal("abc\u0003"u8.ToArray(), stream.Written);
    }

    [Fact]
    public void Writing_returns_at_once_while_the_stream_is_blocked()
    {
        var stream = new GateStream(open: false);
        using var writer = new InputWriter(stream, "test");
        var sw = Stopwatch.StartNew();
        Assert.True(writer.TryWrite("first"u8));
        FakeClaudeHarness.WaitUntil(() => stream.WriteStarted, "writer thread blocked in Write");
        Assert.True(writer.TryWrite("second"u8));
        Assert.True(writer.TryWrite([0x03]));
        Assert.True(sw.Elapsed < Quick, $"took {sw.Elapsed}");

        stream.Open();
        FakeClaudeHarness.WaitUntil(() => stream.Written.Length == 12, "queued bytes written once the stream drains");
        Assert.Equal("firstsecond\u0003"u8.ToArray(), stream.Written);
    }

    [Fact]
    public void A_full_queue_refuses_more_input_until_it_drains()
    {
        var stream = new GateStream(open: false);
        using var writer = new InputWriter(stream, "test", maxQueuedBytes: 10);
        Assert.True(writer.TryWrite("123456"u8));
        Assert.True(writer.TryWrite("7890"u8));
        Assert.False(writer.TryWrite("x"u8));

        stream.Open();
        FakeClaudeHarness.WaitUntil(() => stream.Written.Length == 10, "queue drained");
        Assert.True(writer.TryWrite("x"u8));
    }

    [Fact]
    public void Dispose_does_not_wait_for_a_blocked_write_and_later_writes_are_refused()
    {
        var stream = new GateStream(open: false);
        var writer = new InputWriter(stream, "test");
        Assert.True(writer.TryWrite("stuck"u8));
        FakeClaudeHarness.WaitUntil(() => stream.WriteStarted, "writer thread blocked in Write");

        var sw = Stopwatch.StartNew();
        writer.Dispose();
        Assert.True(sw.Elapsed < Quick, $"took {sw.Elapsed}");
        Assert.False(writer.TryWrite("late"u8));

        // Once the blocked write ends (the pseudo-console closing breaks the pipe), the thread
        // releases the stream.
        stream.Open();
        FakeClaudeHarness.WaitUntil(() => stream.Disposed, "stream disposed by the writer thread");
    }

    [Fact]
    public void A_broken_stream_refuses_further_input()
    {
        var stream = new GateStream(open: true) { Broken = true };
        using var writer = new InputWriter(stream, "test");
        writer.TryWrite("a"u8);
        FakeClaudeHarness.WaitUntil(() => !writer.TryWrite("b"u8), "writes refused after the pipe broke");
    }

    [Fact]
    public async Task Stop_kills_the_job_after_the_grace_period_even_when_the_console_input_never_drains()
    {
        // SPEC 6.4: step 3 is always needed; a console that stops reading input must not keep the
        // stop from getting past step 1.
        GateStream? gate = null;
        using var h = new FakeClaudeHarness();
        h.Configure(new { record = "record.json" });
        using var s = h.Start(wrapInput: inner => gate = new GateStream(open: false, inner));
        h.Record();

        var sw = Stopwatch.StartNew();
        var stop = Task.Run(() => s.StopAsync(TimeSpan.FromMilliseconds(300)));
        var result = await stop.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(result.Killed);
        Assert.Equal(Session.KilledExitCode, result.ExitCode);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");
        Assert.False(FakeClaudeHarness.IsRunning(s.ProcessId));
        Assert.True(gate!.WriteStarted, "the Ctrl+C was queued and the writer is blocked on it");
        gate.Open();
    }

    /// <summary>A write-only stream whose writes block until <see cref="Open"/>; optionally forwards to an inner stream.</summary>
    private sealed class GateStream(bool open, Stream? inner = null) : Stream
    {
        private readonly ManualResetEventSlim _open = new(open);
        private readonly MemoryStream _written = new();
        private volatile bool _writeStarted;

        public bool WriteStarted => _writeStarted;
        public bool Broken { get; init; }
        public bool Disposed { get; private set; }

        public byte[] Written
        {
            get
            {
                lock (_written)
                    return _written.ToArray();
            }
        }

        public void Open() => _open.Set();

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _writeStarted = true;
            _open.Wait();
            if (Broken)
                throw new IOException("pipe broken");
            lock (_written)
                _written.Write(buffer);
            inner?.Write(buffer);
        }

        public override void Flush() => inner?.Flush();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Disposed = true;
                inner?.Dispose();
            }
            base.Dispose(disposing);
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
