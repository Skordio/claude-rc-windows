using System.Text.Json.Serialization;

namespace ClaudeRc.Core.Ipc;

// Host <-> tray messages (SPEC 10). Newline-delimited JSON, UTF-8, every message has "v": 1.
// Encode and decode with IpcCodec; never with reflection-based JsonSerializer calls.

/// <summary>Base of every message on the pipe: carries the protocol version <c>v</c>.</summary>
public abstract record IpcMessage
{
    /// <summary>Protocol version; always <see cref="IpcCodec.Version"/> when sent.</summary>
    [JsonPropertyName("v"), JsonPropertyOrder(-10)]
    public int V { get; init; } = IpcCodec.Version;
}

/// <summary>
/// One row of the main window (SPEC 10): <c>{path, name, on, saved, status, prompt?,
/// exit_code?, restart_in_s?, crashes_10m, started_at?, pid?, error?}</c>. Optional fields are
/// omitted when null.
/// </summary>
public sealed record Row
{
    /// <summary>Normalised folder path; the row's identity.</summary>
    [JsonPropertyName("path")] public required string Path { get; init; }
    /// <summary>Display name (basename, or <c>parent\name</c> for duplicates).</summary>
    [JsonPropertyName("name")] public required string Name { get; init; }
    /// <summary>Desired state (the toggle).</summary>
    [JsonPropertyName("on")] public required bool On { get; init; }
    /// <summary>False for an unsaved row (an external session in a folder not in the list).</summary>
    [JsonPropertyName("saved")] public required bool Saved { get; init; }
    /// <summary>Computed status.</summary>
    [JsonPropertyName("status")] public required FolderStatus Status { get; init; }
    /// <summary>The prompt text while <see cref="FolderStatus.Waiting"/>.</summary>
    [JsonPropertyName("prompt")] public string? Prompt { get; init; }
    /// <summary>Exit code of the last unexpected exit.</summary>
    [JsonPropertyName("exit_code")] public int? ExitCode { get; init; }
    /// <summary>Seconds until the next start while <see cref="FolderStatus.Restarting"/>.</summary>
    [JsonPropertyName("restart_in_s")] public int? RestartInS { get; init; }
    /// <summary>Unexpected exits in the last 10 minutes.</summary>
    [JsonPropertyName("crashes_10m")] public required int Crashes10m { get; init; }
    /// <summary>When the current session process started.</summary>
    [JsonPropertyName("started_at")] public DateTimeOffset? StartedAt { get; init; }
    /// <summary>Process id of the current session (or of the external claude rc).</summary>
    [JsonPropertyName("pid")] public int? Pid { get; init; }
    /// <summary>Why the folder cannot be started (claude not found, <c>.claude.json</c> invalid, …).</summary>
    [JsonPropertyName("error")] public string? Error { get; init; }
}

/// <summary>A request from the tray app; the host answers with an <see cref="IpcResponse"/> carrying the same <see cref="Id"/>.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "op", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(ListRequest), "list")]
[JsonDerivedType(typeof(AddRequest), "add")]
[JsonDerivedType(typeof(SetRequest), "set")]
[JsonDerivedType(typeof(RestartRequest), "restart")]
[JsonDerivedType(typeof(RemoveRequest), "remove")]
[JsonDerivedType(typeof(TakeoverRequest), "takeover")]
[JsonDerivedType(typeof(AttachRequest), "attach")]
[JsonDerivedType(typeof(InputRequest), "input")]
[JsonDerivedType(typeof(ResizeRequest), "resize")]
[JsonDerivedType(typeof(DetachRequest), "detach")]
[JsonDerivedType(typeof(LogRequest), "log")]
[JsonDerivedType(typeof(SettingsGetRequest), "settings.get")]
[JsonDerivedType(typeof(SettingsSetRequest), "settings.set")]
public abstract record IpcRequest : IpcMessage
{
    /// <summary>Request id chosen by the client, echoed in the response.</summary>
    [JsonPropertyName("id"), JsonPropertyOrder(-9)]
    public int Id { get; init; }

    /// <summary>The <c>op</c> name on the wire.</summary>
    [JsonIgnore]
    public abstract string Op { get; }
}

/// <summary><c>list</c>: returns all rows.</summary>
public sealed record ListRequest : IpcRequest
{
    /// <inheritdoc/>
    public override string Op => "list";
}

/// <summary><c>add</c>: save the folder as ON and start it.</summary>
/// <param name="Path">Folder path (the host normalises it and requires an existing directory).</param>
public sealed record AddRequest([property: JsonPropertyName("path")] string Path) : IpcRequest
{
    /// <inheritdoc/>
    public override string Op => "add";
}

/// <summary><c>set</c>: toggle a folder ON or OFF.</summary>
/// <param name="Path">Folder path.</param>
/// <param name="On">New desired state.</param>
public sealed record SetRequest([property: JsonPropertyName("path")] string Path, [property: JsonPropertyName("on")] bool On) : IpcRequest
{
    /// <inheritdoc/>
    public override string Op => "set";
}

/// <summary><c>restart</c>: clear the crash count and start now.</summary>
/// <param name="Path">Folder path.</param>
public sealed record RestartRequest([property: JsonPropertyName("path")] string Path) : IpcRequest
{
    /// <inheritdoc/>
    public override string Op => "restart";
}

/// <summary><c>remove</c>: remove a folder (only when OFF).</summary>
/// <param name="Path">Folder path.</param>
public sealed record RemoveRequest([property: JsonPropertyName("path")] string Path) : IpcRequest
{
    /// <inheritdoc/>
    public override string Op => "remove";
}

/// <summary><c>takeover</c>: stop an external claude rc and start a hosted one (SPEC 7).</summary>
/// <param name="Path">Folder path.</param>
public sealed record TakeoverRequest([property: JsonPropertyName("path")] string Path) : IpcRequest
{
    /// <inheritdoc/>
    public override string Op => "takeover";
}

/// <summary><c>attach</c>: replay screen and scrollback, then stream <see cref="OutputEvent"/>s.</summary>
/// <param name="Path">Folder path.</param>
/// <param name="Cols">Viewer width in columns.</param>
/// <param name="Rows">Viewer height in rows.</param>
public sealed record AttachRequest(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("cols")] int Cols,
    [property: JsonPropertyName("rows")] int Rows) : IpcRequest
{
    /// <inheritdoc/>
    public override string Op => "attach";
}

/// <summary><c>input</c>: send keystrokes to the pseudo-console.</summary>
/// <param name="Path">Folder path.</param>
/// <param name="Data">The keystrokes as text (the host writes them as UTF-8), e.g. <c>"y\r"</c>.</param>
public sealed record InputRequest([property: JsonPropertyName("path")] string Path, [property: JsonPropertyName("data")] string Data) : IpcRequest
{
    /// <inheritdoc/>
    public override string Op => "input";
}

/// <summary><c>resize</c>: resize the pseudo-console.</summary>
/// <param name="Path">Folder path.</param>
/// <param name="Cols">Columns.</param>
/// <param name="Rows">Rows.</param>
public sealed record ResizeRequest(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("cols")] int Cols,
    [property: JsonPropertyName("rows")] int Rows) : IpcRequest
{
    /// <inheritdoc/>
    public override string Op => "resize";
}

/// <summary><c>detach</c>: stop streaming output for a folder.</summary>
/// <param name="Path">Folder path.</param>
public sealed record DetachRequest([property: JsonPropertyName("path")] string Path) : IpcRequest
{
    /// <inheritdoc/>
    public override string Op => "detach";
}

/// <summary><c>log</c>: the last <paramref name="Lines"/> lines of the folder log, in <see cref="IpcResponse.Lines"/>.</summary>
/// <param name="Path">Folder path.</param>
/// <param name="Lines">How many lines.</param>
public sealed record LogRequest([property: JsonPropertyName("path")] string Path, [property: JsonPropertyName("lines")] int Lines) : IpcRequest
{
    /// <inheritdoc/>
    public override string Op => "log";
}

/// <summary><c>settings.get</c>: returns <see cref="IpcResponse.Settings"/> and <see cref="IpcResponse.Info"/>.</summary>
public sealed record SettingsGetRequest : IpcRequest
{
    /// <inheritdoc/>
    public override string Op => "settings.get";
}

/// <summary><c>settings.set</c>: replaces both settings (a null <paramref name="ClaudePath"/> means auto-detect).</summary>
/// <param name="ClaudePath">claude path override, or null.</param>
/// <param name="Notifications">Toasts on or off.</param>
public sealed record SettingsSetRequest(
    [property: JsonPropertyName("claude_path"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ClaudePath,
    [property: JsonPropertyName("notifications")] bool Notifications) : IpcRequest
{
    /// <inheritdoc/>
    public override string Op => "settings.set";
}

/// <summary>The user settings as sent over the pipe.</summary>
/// <param name="ClaudePath">claude path override, or null (always written, also when null).</param>
/// <param name="Notifications">Toasts on or off.</param>
public sealed record IpcSettings(
    [property: JsonPropertyName("claude_path"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ClaudePath,
    [property: JsonPropertyName("notifications")] bool Notifications);

/// <summary>Read-only host facts for the settings window and status bar.</summary>
/// <param name="HostVersion">The host's version.</param>
/// <param name="DataDir">Data folder in use.</param>
/// <param name="LogDir">Log folder in use.</param>
/// <param name="ClaudePath">The claude the host found (what <c>ClaudeLaunch.FoundAt</c> names), if any.</param>
/// <param name="ClaudeVersion">Output of <c>claude --version</c>, if known.</param>
/// <param name="ClaudeError">Why claude was not found, if it was not.</param>
public sealed record HostInfo(
    [property: JsonPropertyName("host_version")] string HostVersion,
    [property: JsonPropertyName("data_dir")] string DataDir,
    [property: JsonPropertyName("log_dir")] string LogDir,
    [property: JsonPropertyName("claude_path")] string? ClaudePath = null,
    [property: JsonPropertyName("claude_version")] string? ClaudeVersion = null,
    [property: JsonPropertyName("claude_error")] string? ClaudeError = null);

/// <summary>The host's reply to an <see cref="IpcRequest"/>: <c>{"v","id","ok","error"?, …}</c>.</summary>
public sealed record IpcResponse : IpcMessage
{
    /// <summary>The request's id; null only for a connection-level error (e.g. version mismatch).</summary>
    [JsonPropertyName("id"), JsonPropertyOrder(-9)] public int? Id { get; init; }
    /// <summary>Whether the request succeeded.</summary>
    [JsonPropertyName("ok"), JsonPropertyOrder(-8)] public required bool Ok { get; init; }
    /// <summary>Message for the user when <see cref="Ok"/> is false.</summary>
    [JsonPropertyName("error")] public string? Error { get; init; }
    /// <summary>All rows, for <c>list</c>.</summary>
    [JsonPropertyName("rows")] public IReadOnlyList<Row>? Rows { get; init; }
    /// <summary>Log lines, for <c>log</c>.</summary>
    [JsonPropertyName("lines")] public IReadOnlyList<string>? Lines { get; init; }
    /// <summary>Settings, for <c>settings.get</c>.</summary>
    [JsonPropertyName("settings")] public IpcSettings? Settings { get; init; }
    /// <summary>Host facts, for <c>settings.get</c> (and optionally <c>list</c>).</summary>
    [JsonPropertyName("info")] public HostInfo? Info { get; init; }
}

/// <summary>A message the host pushes without a request (no <c>id</c>).</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "event", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(RowsEvent), "rows")]
[JsonDerivedType(typeof(OutputEvent), "output")]
[JsonDerivedType(typeof(ToastEvent), "toast")]
public abstract record IpcEvent : IpcMessage;

/// <summary><c>rows</c>: all rows, pushed on any status change.</summary>
/// <param name="Rows">Every row.</param>
public sealed record RowsEvent([property: JsonPropertyName("rows")] IReadOnlyList<Row> Rows) : IpcEvent;

/// <summary><c>output</c>: console bytes for an attached viewer (base64 on the wire).</summary>
/// <param name="Path">Folder path.</param>
/// <param name="Data">Raw pseudo-console output.</param>
public sealed record OutputEvent([property: JsonPropertyName("path")] string Path, [property: JsonPropertyName("data")] byte[] Data) : IpcEvent;

/// <summary><c>toast</c>: ask the tray app to show a notification (SPEC 3.5).</summary>
/// <param name="Path">Folder path.</param>
/// <param name="Status">The status the folder entered.</param>
/// <param name="Text">Notification text.</param>
public sealed record ToastEvent(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("status")] FolderStatus Status,
    [property: JsonPropertyName("text")] string Text) : IpcEvent;
