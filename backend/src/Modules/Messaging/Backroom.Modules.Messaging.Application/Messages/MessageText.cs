using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Backroom.Modules.Messaging.Application.Messages;

/// <summary>
/// The plain text of a message (docs/contracts/phase-1.md, flow 4, step 2): trimmed, NFC, no lone surrogates, no
/// control characters other than line breaks and tabs, 1 to <see cref="MaxLength"/> characters. A plain send gives
/// it directly and the server derives the blocks from it (one paragraph); a rich text send gives blocks and the
/// server derives this text from them (<see cref="MessageBlocks"/>), under the same rules. Nothing is HTML-escaped on
/// the way in: <c>text</c> is the plain-text form (notifications, search, fallback) and the blocks hold plain strings;
/// escaping happens when a client renders them (React text nodes do it).
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

        // Trim() and Normalize() are culture-independent. NFC can lengthen some strings, so the limit is checked after
        // normalizing; the work is bounded first by a generous cap on the input.
        var trimmed = raw.Trim();
        if (trimmed.Length is 0 or > MaxLength * 4 || !TextRules.IsClean(trimmed, allowLayoutControls: true))
        {
            return false;
        }

        // Line endings become line feeds, so a text reads the same everywhere.
        var normalized = trimmed.Normalize(NormalizationForm.FormC).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (normalized.Length > MaxLength)
        {
            return false;
        }

        text = normalized;
        return true;
    }

    /// <summary>The stored blocks of a plain text: one paragraph holding the text as one text node, as JSON.</summary>
    public static string ToBlocks(string text) => MessageBlocks.ToJson(MessageBlocks.FromPlainText(text).Blocks);
}
