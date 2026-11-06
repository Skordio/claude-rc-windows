using ClaudeRc.Core.Logging;
using Microsoft.Extensions.Time.Testing;

namespace ClaudeRc.Core.Tests;

public sealed class RollingLogTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 6, 10, 13, 14, 123, TimeSpan.Zero));

    public void Dispose() => _tmp.Dispose();

    private static string[] Lines(string path) => File.ReadAllText(path).Split('\n', StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void Lines_are_timestamped_and_the_folder_is_created()
    {
        var log = new RollingLog(_tmp.Combine("logs", "host.log"), 1000, 3, _time);
        log.Write("host started");
        _time.Advance(TimeSpan.FromSeconds(1));
        log.Write("second");
        Assert.Equal(["2026-11-06T10:13:14.123+00:00 host started", "2026-11-06T10:13:15.123+00:00 second"], Lines(log.FilePath));
    }

    [Fact]
    public void Timestamps_use_local_time()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("plus2", TimeSpan.FromHours(2), "plus2", "plus2"));
        var log = new RollingLog(_tmp.Combine("a.log"), 1000, 3, _time);
        log.Write("x");
        Assert.Equal(["2026-11-06T12:13:14.123+02:00 x"], Lines(log.FilePath));
    }

    [Fact]
    public void Multi_line_messages_stay_on_one_line()
    {
        var log = new RollingLog(_tmp.Combine("a.log"), 1000, 3, _time);
        log.Write("one\r\ntwo\nthree");
        Assert.Equal(["2026-11-06T10:13:14.123+00:00 one two three"], Lines(log.FilePath));
    }

    [Fact]
    public void Header_is_the_first_line_of_every_file()
    {
        var log = new RollingLog(_tmp.Combine("f.log"), 200, 2, _time, header: @"# folder: C:\src\app");
        for (var i = 0; i < 20; i++)
            log.Write($"event {i:00} with some padding text");
        Assert.True(File.Exists(log.FilePath + ".1"));
        Assert.Equal(@"# folder: C:\src\app", Lines(log.FilePath)[0]);
        Assert.Equal(@"# folder: C:\src\app", Lines(log.FilePath + ".1")[0]);
    }

    [Fact]
    public void Rolls_at_the_size_limit_and_keeps_the_given_number_of_files()
    {
        var log = new RollingLog(_tmp.Combine("host.log"), 300, 3, _time);
        for (var i = 0; i < 100; i++)
            log.Write($"event {i:000} with some padding text");

        Assert.Equal(
            ["host.log", "host.log.1", "host.log.2"],
            Directory.GetFiles(_tmp.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        foreach (var f in Directory.GetFiles(_tmp.Path))
            Assert.True(new FileInfo(f).Length <= 300, f);
        Assert.EndsWith("event 099 with some padding text", Lines(log.FilePath)[^1]);
    }

    [Fact]
    public void Last_lines_span_rolled_files_and_skip_headers()
    {
        var log = new RollingLog(_tmp.Combine("f.log"), 200, 2, _time, header: "# folder: x");
        for (var i = 0; i < 12; i++)
            log.Write($"event {i:00} padding padding");

        var last = log.ReadLastLines(5);
        Assert.Equal(5, last.Count);
        Assert.Equal(["event 07", "event 08", "event 09", "event 10", "event 11"], last.Select(l => l[30..38]));
        Assert.DoesNotContain(log.ReadLastLines(1000), l => l.StartsWith('#'));
    }

    [Fact]
    public void Last_lines_of_a_missing_log_is_empty()
    {
        var log = new RollingLog(_tmp.Combine("none.log"), 200, 2, _time);
        Assert.Empty(log.ReadLastLines(10));
    }

    [Fact]
    public void Log_can_be_read_while_it_is_written()
    {
        var log = new RollingLog(_tmp.Combine("a.log"), 1000, 2, _time);
        log.Write("one");
        using var reader = new FileStream(log.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        log.Write("two");
        Assert.Equal(2, Lines(log.FilePath).Length);
    }

    [Fact]
    public void Folder_hash_is_the_first_16_hex_chars_of_sha256()
    {
        Assert.Equal("223479b1852924db", LogFiles.FolderHash(@"C:\src\app"));
        Assert.NotEqual(LogFiles.FolderHash(@"C:\src\app"), LogFiles.FolderHash(@"C:\src\App"));
    }

    [Fact]
    public void Standard_logs_use_the_spec_names_and_limits()
    {
        var host = LogFiles.HostLog(_tmp.Combine("logs"), _time);
        Assert.Equal(_tmp.Combine("logs", "host.log"), host.FilePath);
        Assert.Equal(5 * 1024 * 1024, host.MaxBytes);
        Assert.Equal(3, host.KeepFiles);

        var folder = LogFiles.FolderLog(_tmp.Combine("logs", "folders"), @"C:\src\app", _time);
        Assert.Equal(_tmp.Combine("logs", "folders", "223479b1852924db.log"), folder.FilePath);
        Assert.Equal(1024 * 1024, folder.MaxBytes);
        Assert.Equal(2, folder.KeepFiles);

        folder.Write("started");
        Assert.Equal(@"# folder: C:\src\app", Lines(folder.FilePath)[0]);
    }
}
