using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Backroom.Modules.Messaging.Application.Messages;

/// <summary>
/// The text of a message (docs/contracts/phase-1.md, flow 4, step 2). The server validates it (trimmed, NFC, no lone
/// surrogates, no control characters other than line breaks and tabs, 1 to <see cref="MaxLength"/> characters) and
/// derives the blocks from it; blocks sent by a client are never read. Nothing is HTML-escaped on the way in:
/// <c>text</c> is the plain-text form (notifications, search, fallback) and the blocks hold plain strings; escaping
/// happens when a client renders them (React text nodes do it).
/// <para>
/// Blocks shape: a JSON array of block nodes, each <c>{ "type": ..., "children": [...] }</c>. Phase 1 has one block,
/// <c>paragraph</c>, with one inline <c>{ "type": "text", "text": "..." }</c>. Rich text adds typed nodes without
/// changing this: marks on text nodes (<c>"marks": [{ "type": "bold" | "italic" | "strike" | "code" }, { "type": "link",
/// "href": ... }]</c>), inline nodes (<c>mention</c> with an account id), and block types (<c>bulleted_list</c>,
/// <c>numbered_list</c>, <c>list_item</c>, <c>quote</c>, <c>code_block</c>) whose children are blocks or inlines.
/// A reader skips node types it does not know.
/// </para>
/// </summary>
internal static class MessageText
{
    /// <summary>The longest text a person may send, in UTF-16 characters after normalization.</summary>
    public const int MaxLength = 4000;

    /// <summary>Validates <paramref name="raw"/> and returns the text to store.</summary>
    public static bool TryPrepare(string? raw, [NotNullWhen(true)] out string? text)
    {
        text = null;
        if (raw is null)
        {
            return false;
        }

        // Trim() and Normalize() are culture-independent. Composition only shortens a text, but bound the work first.
        var trimmed = raw.Trim();
        if (trimmed.Length is 0 or > MaxLength * 4 || !TextRules.IsClean(trimmed, allowLayoutControls: true))
        {
            return false;
        }

        var normalized = trimmed.Normalize(NormalizationForm.FormC);
        if (normalized.Length > MaxLength)
        {
            return false;
        }

        text = normalized;
        return true;
    }

    /// <summary>The stored blocks: one paragraph holding the text as one text node, as JSON.</summary>
    public static string ToBlocks(string text)
    {
        using var buffer = new MemoryStream(text.Length + 80);
        using (var writer = new System.Text.Json.Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("type", "paragraph");
            writer.WriteStartArray("children");
            writer.WriteStartObject();
            writer.WriteString("type", "text");
            writer.WriteString("text", text);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}
