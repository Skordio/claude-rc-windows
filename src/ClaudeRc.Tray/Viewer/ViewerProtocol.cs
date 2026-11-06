using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeRc.Tray.Viewer;

// Messages between the console viewer page (Viewer/web/viewer.js) and the app, as JSON through
// WebView2's postMessage. The page never talks to the host: the app turns these into pipe requests.

/// <summary>A message from the page.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(PageReady), "ready")]
[JsonDerivedType(typeof(PageInput), "input")]
[JsonDerivedType(typeof(PageResize), "resize")]
[JsonDerivedType(typeof(PageCopy), "copy")]
internal abstract record PageMessage;

/// <summary>The terminal is open and fitted to the window: <paramref name="Cols"/> × <paramref name="Rows"/>.</summary>
internal sealed record PageReady([property: JsonPropertyName("cols")] int Cols, [property: JsonPropertyName("rows")] int Rows) : PageMessage;

/// <summary>Keystrokes (or pasted text) from xterm's <c>onData</c>.</summary>
internal sealed record PageInput([property: JsonPropertyName("data")] string Data) : PageMessage;

/// <summary>The terminal was fitted to a new window size.</summary>
internal sealed record PageResize([property: JsonPropertyName("cols")] int Cols, [property: JsonPropertyName("rows")] int Rows) : PageMessage;

/// <summary>Ctrl+C with a selection: put <paramref name="Text"/> on the clipboard.</summary>
internal sealed record PageCopy([property: JsonPropertyName("text")] string Text) : PageMessage;

/// <summary>A message to the page.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(ViewerOutput), "output")]
[JsonDerivedType(typeof(ViewerStatus), "status")]
[JsonDerivedType(typeof(ViewerFocus), "focus")]
internal abstract record ViewerMessage;

/// <summary>Console bytes, exactly as the host sent them (base64 in JSON); the page writes them to xterm as bytes.</summary>
internal sealed record ViewerOutput([property: JsonPropertyName("data")] byte[] Data) : ViewerMessage;

/// <summary>The banner above the terminal; a null <paramref name="Text"/> hides it.</summary>
/// <param name="Text">What to show, or null.</param>
/// <param name="Level"><c>info</c> or <c>error</c>.</param>
internal sealed record ViewerStatus(
    [property: JsonPropertyName("text"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Text,
    [property: JsonPropertyName("level")] string Level) : ViewerMessage;

/// <summary>Give the terminal the keyboard focus.</summary>
internal sealed record ViewerFocus : ViewerMessage;

/// <summary>Encodes and decodes viewer messages.</summary>
internal static class ViewerProtocol
{
    /// <summary>Decodes a message from the page; null when it is not a valid one (it is then ignored).</summary>
    public static PageMessage? Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, ViewerJsonContext.Default.PageMessage);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Encodes a message for <c>PostWebMessageAsJson</c>.</summary>
    public static string Encode(ViewerMessage message) => JsonSerializer.Serialize(message, ViewerJsonContext.Default.ViewerMessage);
}

[JsonSourceGenerationOptions(RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true, AllowOutOfOrderMetadataProperties = true)]
[JsonSerializable(typeof(PageMessage))]
[JsonSerializable(typeof(ViewerMessage))]
internal sealed partial class ViewerJsonContext : JsonSerializerContext;
