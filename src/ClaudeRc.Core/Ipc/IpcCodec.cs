using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeRc.Core.Ipc;

/// <summary>The other side speaks a different protocol version (or none); close the connection.</summary>
public sealed class IpcVersionException(string message) : Exception(message);

/// <summary>A line on the pipe is not a valid message.</summary>
public sealed class IpcFormatException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Encodes and decodes pipe messages (SPEC 10): one JSON object per line, UTF-8, with
/// <c>"v": 1</c>. Encoded lines never contain a newline; the caller writes <c>line + "\n"</c>.
/// </summary>
public static class IpcCodec
{
    /// <summary>The protocol version this build speaks.</summary>
    public const int Version = 1;

    /// <summary>Shown by the tray app (and sent by the host) on a version mismatch.</summary>
    public const string VersionMismatchMessage = "host and app versions differ: restart the host";

    /// <summary>Encodes a request (tray → host).</summary>
    public static string Encode(IpcRequest request) => JsonSerializer.Serialize(request, IpcJsonContext.Default.IpcRequest);

    /// <summary>Encodes a response (host → tray).</summary>
    public static string Encode(IpcResponse response) => JsonSerializer.Serialize(response, IpcJsonContext.Default.IpcResponse);

    /// <summary>Encodes an event (host → tray).</summary>
    public static string Encode(IpcEvent ipcEvent) => JsonSerializer.Serialize(ipcEvent, IpcJsonContext.Default.IpcEvent);

    /// <summary>Decodes a request line (host side).</summary>
    /// <exception cref="IpcVersionException"><c>v</c> is missing or not <see cref="Version"/>.</exception>
    /// <exception cref="IpcFormatException">Not a valid request.</exception>
    public static IpcRequest DecodeRequest(string line)
    {
        CheckVersion(line, out _);
        return Deserialize(line, IpcJsonContext.Default.IpcRequest);
    }

    /// <summary>
    /// Decodes a line from the host (tray side): an <see cref="IpcResponse"/>, or an
    /// <see cref="IpcEvent"/> when the object has an <c>event</c> property.
    /// </summary>
    /// <exception cref="IpcVersionException"><c>v</c> is missing or not <see cref="Version"/>.</exception>
    /// <exception cref="IpcFormatException">Not a valid host message.</exception>
    public static IpcMessage DecodeHostMessage(string line)
    {
        CheckVersion(line, out var isEvent);
        return isEvent
            ? Deserialize(line, IpcJsonContext.Default.IpcEvent)
            : Deserialize(line, IpcJsonContext.Default.IpcResponse);
    }

    private static void CheckVersion(string line, out bool isEvent)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new IpcFormatException("message is not a JSON object");
            if (!doc.RootElement.TryGetProperty("v", out var v) || v.ValueKind != JsonValueKind.Number
                || !v.TryGetInt32(out var version) || version != Version)
                throw new IpcVersionException(VersionMismatchMessage);
            isEvent = doc.RootElement.TryGetProperty("event", out _);
        }
        catch (JsonException e)
        {
            throw new IpcFormatException("message is not valid JSON: " + e.Message, e);
        }
    }

    private static T Deserialize<T>(string line, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
    {
        try
        {
            return JsonSerializer.Deserialize(line, type) ?? throw new IpcFormatException("message is null");
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException)
        {
            throw new IpcFormatException("invalid message: " + e.Message, e);
        }
    }
}

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    AllowOutOfOrderMetadataProperties = true,
    RespectRequiredConstructorParameters = true,
    RespectNullableAnnotations = true)]
[JsonSerializable(typeof(IpcRequest))]
[JsonSerializable(typeof(IpcResponse))]
[JsonSerializable(typeof(IpcEvent))]
internal sealed partial class IpcJsonContext : JsonSerializerContext;
