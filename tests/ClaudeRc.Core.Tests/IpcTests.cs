using System.Text.Json.Nodes;
using ClaudeRc.Core.Ipc;

namespace ClaudeRc.Core.Tests;

public sealed class IpcTests
{
    [Fact]
    public void Full_row_has_exactly_the_spec_fields_in_order()
    {
        var row = new Row
        {
            Path = @"C:\src\app",
            Name = "app",
            On = true,
            Saved = true,
            Status = FolderStatus.Restarting,
            Prompt = "Continue? [y/N]",
            ExitCode = -1073741510,
            RestartInS = 20,
            Crashes10m = 3,
            StartedAt = new DateTimeOffset(2026, 11, 6, 10, 13, 14, TimeSpan.Zero),
            Pid = 4242,
            Error = "boom",
        };

        var json = JsonNode.Parse(IpcCodec.Encode(new RowsEvent([row])))!["rows"]![0]!.AsObject();

        Assert.Equal(
            ["path", "name", "on", "saved", "status", "prompt", "exit_code", "restart_in_s", "crashes_10m", "started_at", "pid", "error"],
            json.Select(p => p.Key));
        Assert.Equal(@"C:\src\app", (string)json["path"]!);
        Assert.Equal("restarting", (string)json["status"]!);
        Assert.Equal(-1073741510, (int)json["exit_code"]!);
        Assert.Equal(20, (int)json["restart_in_s"]!);
        Assert.Equal(3, (int)json["crashes_10m"]!);
        Assert.Equal("2026-11-06T10:13:14+00:00", (string)json["started_at"]!);
        Assert.Equal(4242, (int)json["pid"]!);
    }

    [Fact]
    public void Optional_row_fields_are_omitted_when_empty()
    {
        var row = new Row { Path = @"C:\a", Name = "a", On = false, Saved = true, Status = FolderStatus.Off, Crashes10m = 0 };
        var line = IpcCodec.Encode(new RowsEvent([row]));
        Assert.Equal("""{"event":"rows","v":1,"rows":[{"path":"C:\\a","name":"a","on":false,"saved":true,"status":"off","crashes_10m":0}]}""", line);
    }

    [Theory]
    [InlineData(FolderStatus.Off, "off")]
    [InlineData(FolderStatus.Starting, "starting")]
    [InlineData(FolderStatus.Running, "running")]
    [InlineData(FolderStatus.Waiting, "waiting")]
    [InlineData(FolderStatus.Restarting, "restarting")]
    [InlineData(FolderStatus.Failing, "failing")]
    [InlineData(FolderStatus.Missing, "missing")]
    [InlineData(FolderStatus.External, "external")]
    public void Statuses_use_the_spec_names(FolderStatus status, string name)
    {
        var line = IpcCodec.Encode(new ToastEvent(@"C:\a", status, "t"));
        Assert.Equal(name, (string)JsonNode.Parse(line)!["status"]!);
        Assert.Equal(status, ((ToastEvent)IpcCodec.DecodeHostMessage(line)).Status);
    }

    private static readonly IpcRequest[] Requests =
    [
        new ListRequest { Id = 1 },
        new AddRequest(@"C:\a") { Id = 2 },
        new SetRequest(@"C:\a", false) { Id = 3 },
        new RestartRequest(@"C:\a") { Id = 4 },
        new RemoveRequest(@"C:\a") { Id = 5 },
        new TakeoverRequest(@"C:\a") { Id = 6 },
        new AttachRequest(@"C:\a", 120, 40) { Id = 7 },
        new InputRequest(@"C:\a", "y\r") { Id = 8 },
        new ResizeRequest(@"C:\a", 100, 30) { Id = 9 },
        new DetachRequest(@"C:\a") { Id = 10 },
        new LogRequest(@"C:\a", 50) { Id = 11 },
        new SettingsGetRequest { Id = 12 },
        new SettingsSetRequest(null, false) { Id = 13 },
    ];

    public static TheoryData<IpcRequest> AllRequests() => new(Requests);

    [Theory]
    [MemberData(nameof(AllRequests))]
    public void Every_request_round_trips_with_v1_and_its_op(IpcRequest request)
    {
        var line = IpcCodec.Encode(request);
        Assert.DoesNotContain('\n', line);
        var json = JsonNode.Parse(line)!.AsObject();
        Assert.Equal(1, (int)json["v"]!);
        Assert.Equal(request.Op, (string)json["op"]!);
        Assert.Equal(request, IpcCodec.DecodeRequest(line));
    }

    [Theory]
    [InlineData("list", typeof(ListRequest))]
    [InlineData("add", typeof(AddRequest))]
    [InlineData("set", typeof(SetRequest))]
    [InlineData("restart", typeof(RestartRequest))]
    [InlineData("remove", typeof(RemoveRequest))]
    [InlineData("takeover", typeof(TakeoverRequest))]
    [InlineData("attach", typeof(AttachRequest))]
    [InlineData("input", typeof(InputRequest))]
    [InlineData("resize", typeof(ResizeRequest))]
    [InlineData("detach", typeof(DetachRequest))]
    [InlineData("log", typeof(LogRequest))]
    [InlineData("settings.get", typeof(SettingsGetRequest))]
    [InlineData("settings.set", typeof(SettingsSetRequest))]
    public void Op_names_match_the_spec(string op, Type type)
    {
        var request = Requests.Single(r => r.GetType() == type);
        Assert.Equal(op, request.Op);
    }

    [Fact]
    public void Hand_written_request_with_op_not_first_is_decoded()
    {
        var r = IpcCodec.DecodeRequest("""{"v":1,"id":7,"path":"C:\\a","cols":120,"rows":40,"op":"attach"}""");
        Assert.Equal(new AttachRequest(@"C:\a", 120, 40) { Id = 7 }, r);
    }

    [Fact]
    public void Settings_set_keeps_an_explicit_null_claude_path()
    {
        var json = JsonNode.Parse(IpcCodec.Encode(new SettingsSetRequest(null, true) { Id = 1 }))!.AsObject();
        Assert.True(json.ContainsKey("claude_path"));
        Assert.Null(json["claude_path"]);
        Assert.True((bool)json["notifications"]!);
    }

    [Theory]
    [InlineData("""{"v":2,"id":1,"op":"list"}""")]
    [InlineData("""{"id":1,"op":"list"}""")]
    [InlineData("""{"v":"1","id":1,"op":"list"}""")]
    public void Wrong_or_missing_version_is_a_version_error(string line)
    {
        Assert.Throws<IpcVersionException>(() => IpcCodec.DecodeRequest(line));
        Assert.Throws<IpcVersionException>(() => IpcCodec.DecodeHostMessage(line));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"v":1,"id":1,"op":"explode"}""")]
    [InlineData("""{"v":1,"id":1}""")]
    [InlineData("""{"v":1,"id":1,"op":"add"}""")]
    public void Malformed_request_is_a_format_error(string line)
    {
        Assert.Throws<IpcFormatException>(() => IpcCodec.DecodeRequest(line));
    }

    [Fact]
    public void Response_round_trips_and_omits_empty_fields()
    {
        var ok = new IpcResponse { Id = 4, Ok = true };
        Assert.Equal("""{"v":1,"id":4,"ok":true}""", IpcCodec.Encode(ok));

        var err = new IpcResponse { Id = 5, Ok = false, Error = "only when OFF" };
        var decoded = Assert.IsType<IpcResponse>(IpcCodec.DecodeHostMessage(IpcCodec.Encode(err)));
        Assert.False(decoded.Ok);
        Assert.Equal("only when OFF", decoded.Error);
        Assert.Equal(5, decoded.Id);
    }

    [Fact]
    public void List_log_and_settings_responses_carry_their_payloads()
    {
        var row = new Row { Path = @"C:\a", Name = "a", On = true, Saved = true, Status = FolderStatus.Running, Crashes10m = 0 };
        var response = new IpcResponse
        {
            Id = 1,
            Ok = true,
            Rows = [row],
            Lines = ["one", "two"],
            Settings = new IpcSettings(null, true),
            Info = new HostInfo("1.0.0", @"C:\data", @"C:\logs", @"C:\claude.exe", "2.1.318 (Claude Code)", null),
        };

        var line = IpcCodec.Encode(response);
        var json = JsonNode.Parse(line)!.AsObject();
        Assert.True(json["settings"]!.AsObject().ContainsKey("claude_path"));
        Assert.Equal(["host_version", "data_dir", "log_dir", "claude_path", "claude_version"], json["info"]!.AsObject().Select(p => p.Key));

        var decoded = Assert.IsType<IpcResponse>(IpcCodec.DecodeHostMessage(line));
        Assert.Equal(row, Assert.Single(decoded.Rows!));
        Assert.Equal(["one", "two"], decoded.Lines);
        Assert.Equal(new IpcSettings(null, true), decoded.Settings);
        Assert.Equal("2.1.318 (Claude Code)", decoded.Info!.ClaudeVersion);
    }

    [Fact]
    public void Output_event_carries_base64_bytes()
    {
        var line = IpcCodec.Encode(new OutputEvent(@"C:\a", [0x1B, 0x5B, 0x6D, 0x0A]));
        Assert.Equal("""{"event":"output","v":1,"path":"C:\\a","data":"G1ttCg=="}""", line);
        var e = Assert.IsType<OutputEvent>(IpcCodec.DecodeHostMessage(line));
        Assert.Equal([0x1B, 0x5B, 0x6D, 0x0A], e.Data);
    }

    [Fact]
    public void Events_decode_by_their_event_name()
    {
        var rows = IpcCodec.DecodeHostMessage("""{"v":1,"rows":[],"event":"rows"}""");
        Assert.Empty(Assert.IsType<RowsEvent>(rows).Rows);

        var toast = Assert.IsType<ToastEvent>(IpcCodec.DecodeHostMessage("""{"v":1,"event":"toast","path":"C:\\a","status":"failing","text":"x"}"""));
        Assert.Equal(FolderStatus.Failing, toast.Status);
    }

    [Fact]
    public void Unknown_host_message_is_a_format_error()
    {
        Assert.Throws<IpcFormatException>(() => IpcCodec.DecodeHostMessage("""{"v":1,"event":"nope"}"""));
        Assert.Throws<IpcFormatException>(() => IpcCodec.DecodeHostMessage("{"));
    }

    [Fact]
    public void Version_mismatch_message_is_the_spec_text()
    {
        Assert.Equal("host and app versions differ: restart the host", IpcCodec.VersionMismatchMessage);
        Assert.Equal(1, IpcCodec.Version);
    }
}
