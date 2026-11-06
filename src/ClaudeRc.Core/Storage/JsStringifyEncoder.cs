using System.Text.Encodings.Web;

namespace ClaudeRc.Core.Storage;

/// <summary>
/// A <see cref="JavaScriptEncoder"/> that escapes exactly what JavaScript's
/// <c>JSON.stringify</c> escapes: <c>"</c>, <c>\</c> and control characters below U+0020
/// (<c>\b \f \n \r \t</c>, otherwise <c>\u00xx</c> in lower-case hex). Everything else,
/// including non-ASCII, emoji and U+2028, is written as is. Used to rewrite files that claude
/// itself writes (<c>.claude.json</c>) with the smallest possible diff.
/// </summary>
internal sealed class JsStringifyEncoder : JavaScriptEncoder
{
    public static readonly JsStringifyEncoder Instance = new();

    private JsStringifyEncoder() { }

    public override int MaxOutputCharactersPerInputCharacter => 6;

    public override bool WillEncode(int unicodeScalar) =>
        unicodeScalar < 0x20 || unicodeScalar == '"' || unicodeScalar == '\\';

    public override unsafe int FindFirstCharacterToEncode(char* text, int textLength)
    {
        for (var i = 0; i < textLength; i++)
        {
            var c = text[i];
            if (c < 0x20 || c == '"' || c == '\\')
                return i;
        }
        return -1;
    }

    public override unsafe bool TryEncodeUnicodeScalar(int unicodeScalar, char* buffer, int bufferLength, out int numberOfCharactersWritten)
    {
        var text = !WillEncode(unicodeScalar)
            ? char.ConvertFromUtf32(unicodeScalar)
            : unicodeScalar switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                '\b' => "\\b",
                '\f' => "\\f",
                _ => "\\u" + unicodeScalar.ToString("x4", System.Globalization.CultureInfo.InvariantCulture),
            };
        if (text.Length > bufferLength)
        {
            numberOfCharactersWritten = 0;
            return false;
        }
        for (var i = 0; i < text.Length; i++)
            buffer[i] = text[i];
        numberOfCharactersWritten = text.Length;
        return true;
    }
}
