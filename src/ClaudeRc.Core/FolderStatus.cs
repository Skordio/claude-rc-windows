using System.Text.Json.Serialization;

namespace ClaudeRc.Core;

/// <summary>A folder's status as computed by the host (SPEC 4). Serialised as the lower-case name.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FolderStatus>))]
public enum FolderStatus
{
    /// <summary>Turned off.</summary>
    [JsonStringEnumMemberName("off")] Off,
    /// <summary>Process launched (or about to be), not yet seen ready; at most 30 s, then <see cref="Running"/>.</summary>
    [JsonStringEnumMemberName("starting")] Starting,
    /// <summary>Session process alive and not at a prompt.</summary>
    [JsonStringEnumMemberName("running")] Running,
    /// <summary>Alive, but the console ends at an unanswered prompt (SPEC 8).</summary>
    [JsonStringEnumMemberName("waiting")] Waiting,
    /// <summary>Exited (or could not be started); waiting out the delay before the next start.</summary>
    [JsonStringEnumMemberName("restarting")] Restarting,
    /// <summary>Too many exits (SPEC 5.2); no automatic starts until Restart.</summary>
    [JsonStringEnumMemberName("failing")] Failing,
    /// <summary>The folder does not exist; re-checked (SPEC 5.3).</summary>
    [JsonStringEnumMemberName("missing")] Missing,
    /// <summary>A claude rc started outside the app runs in this folder (SPEC 7).</summary>
    [JsonStringEnumMemberName("external")] External,
}
