using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Backroom.Modules.Messaging.Application.Messages;

/// <summary>
/// The text of a message (docs/contracts/phase-1.md, flow 4, step 2): the server trims it, refuses an empty or
/// too long one and control characters other than line breaks and tabs, and stores it HTML-escaped. The blocks are
/// always derived from it here; blocks sent by a client are never read.
/// </summary>
internal static class MessageText
{
    /// <summary>The longest text a person may send, in UTF-16 characters, before escaping.</summary>
    public const int MaxLength = 4000;

    /// <summary>The longest escaped text: every character at most six long (<c>&amp;quot;</c>). Also the database limit.</summary>
    public const int MaxStoredLength = MaxLength * 6;

    private static readonly SearchValues<char> s_escaped = SearchValues.Create("&<>\"'");

    /// <summary>Validates <paramref name="raw"/> and returns the escaped text to store.</summary>
    public static bool TryPrepare(string? raw, [NotNullWhen(true)] out string? stored)
    {
        stored = null;
        if (raw is null)
        {
            return false;
        }

        // Trim() is culture-independent; check the length before any work on a huge string.
        var text = raw.Trim();
        if (text.Length is 0 or > MaxLength || !TextRules.IsClean(text, allowLayoutControls: true))
        {
            return false;
        }

        stored = Escape(text.Replace("\r\n", "\n", StringComparison.Ordinal));
        return true;
    }

    /// <summary>The stored blocks: one paragraph holding the escaped text, as JSON.</summary>
    public static string ToBlocks(string stored)
    {
        using var buffer = new MemoryStream(stored.Length + 48);
        using (var writer = new System.Text.Json.Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("type", "paragraph");
            writer.WriteString("text", stored);
            writer.WriteEndObject();
            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static string Escape(string text)
    {
        if (text.AsSpan().IndexOfAny(s_escaped) < 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length + 16);
        foreach (var character in text)
        {
            switch (character)
            {
                case '&':
                    builder.Append("&amp;");
                    break;
                case '<':
                    builder.Append("&lt;");
                    break;
                case '>':
                    builder.Append("&gt;");
                    break;
                case '"':
                    builder.Append("&quot;");
                    break;
                case '\'':
                    builder.Append("&#39;");
                    break;
                default:
                    builder.Append(character);
                    break;
            }
        }

        return builder.ToString();
    }
}
