using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeRc.Core.Storage;

namespace ClaudeRc.Core.Claude;

/// <summary>Outcome of <see cref="ClaudeJsonPresets.Ensure"/>.</summary>
public enum PresetOutcome
{
    /// <summary>Both presets were already in place; the file was not written.</summary>
    Unchanged,
    /// <summary>The file was updated (or created).</summary>
    Written,
    /// <summary>The file could not be read, parsed or written; it was left as it was.</summary>
    Error,
}

/// <summary>Result of <see cref="ClaudeJsonPresets.Ensure"/>.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Error">Message for the row when <paramref name="Outcome"/> is <see cref="PresetOutcome.Error"/>.</param>
public sealed record PresetResult(PresetOutcome Outcome, string? Error = null);

/// <summary>
/// Pre-answers claude rc's per-folder prompts in <c>.claude.json</c> before each start
/// (SPEC 6.2): sets <c>projects[key].hasTrustDialogAccepted = true</c> and, only if absent,
/// <c>projects[key].remoteControlSpawnMode = "same-dir"</c>. Nothing else is changed.
/// </summary>
/// <remarks>
/// The file is rewritten in claude's own style (2-space indent, LF, no BOM, no trailing
/// newline, <c>JSON.stringify</c> escaping, key order and number spelling kept) and replaced
/// atomically. It is not written when nothing changes, and never touched when it is not
/// valid JSON. Not thread-safe: the host calls it from one place.
/// </remarks>
/// <param name="claudeJsonPath">The file to edit (default <c>%USERPROFILE%\.claude.json</c>; tests pass a temp file).</param>
public sealed class ClaudeJsonPresets(string claudeJsonPath)
{
    /// <summary>The key that claude rc reads trust and spawn mode from.</summary>
    public const string TrustKey = "hasTrustDialogAccepted";

    /// <summary>The key for the saved spawn mode; kept when present.</summary>
    public const string SpawnModeKey = "remoteControlSpawnMode";

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>The <c>.claude.json</c> file this instance edits.</summary>
    public string FilePath { get; } = claudeJsonPath;

    /// <summary>
    /// The <c>projects</c> key claude uses for a working directory: the string exactly as passed
    /// to claude with every <c>\</c> replaced by <c>/</c>, no case change (m0-findings 3). Pass the
    /// normalised path that is also used as the session's working directory.
    /// </summary>
    public static string ProjectKey(string normalizedPath) => normalizedPath.Replace('\\', '/');

    /// <summary>Makes sure both presets are set for <paramref name="normalizedFolderPath"/>.</summary>
    public PresetResult Ensure(string normalizedFolderPath)
    {
        var key = ProjectKey(normalizedFolderPath);
        try
        {
            string? original = File.Exists(FilePath) ? File.ReadAllText(FilePath, Utf8NoBom) : null;
            JsonObject root;
            if (original is null)
            {
                root = [];
            }
            else
            {
                JsonNode? parsed;
                try
                {
                    parsed = JsonNode.Parse(original, documentOptions: new JsonDocumentOptions { MaxDepth = 256 });
                }
                catch (JsonException e)
                {
                    return Fail($"{FilePath} is not valid JSON ({e.Message}); not changed");
                }
                if (parsed is not JsonObject obj)
                    return Fail($"{FilePath} is not a JSON object; not changed");
                root = obj;
            }

            if (root["projects"] is null)
            {
                if (root.ContainsKey("projects"))
                    return Fail($"{FilePath}: \"projects\" is null; not changed");
                root["projects"] = new JsonObject();
            }
            if (root["projects"] is not JsonObject projects)
                return Fail($"{FilePath}: \"projects\" is not an object; not changed");

            // Ordinal (case-sensitive) lookup, like claude's.
            if (!projects.TryGetPropertyValue(key, out var projectNode) || projectNode is null)
            {
                projectNode = new JsonObject();
                projects[key] = projectNode;
            }
            if (projectNode is not JsonObject project)
                return Fail($"{FilePath}: project \"{key}\" is not an object; not changed");

            var changed = original is null;
            if (project[TrustKey] is not JsonValue trust || trust.GetValueKind() != JsonValueKind.True)
            {
                project[TrustKey] = true;
                changed = true;
            }
            if (!project.ContainsKey(SpawnModeKey))
            {
                project[SpawnModeKey] = "same-dir";
                changed = true;
            }
            if (!changed)
                return new PresetResult(PresetOutcome.Unchanged);

            AtomicFile.WriteAllText(FilePath, Serialize(root));
            return new PresetResult(PresetOutcome.Written);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return Fail($"could not update {FilePath}: {e.Message}");
        }
    }

    /// <summary>Serialises <paramref name="node"/> the way claude (<c>JSON.stringify(x, null, 2)</c>) does.</summary>
    internal static string Serialize(JsonNode node)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = true,
            IndentSize = 2,
            NewLine = "\n",
            Encoder = JsStringifyEncoder.Instance,
            MaxDepth = 256,
        }))
        {
            node.WriteTo(writer);
        }
        return Utf8NoBom.GetString(buffer.ToArray());
    }

    private static PresetResult Fail(string message) => new(PresetOutcome.Error, message);
}
