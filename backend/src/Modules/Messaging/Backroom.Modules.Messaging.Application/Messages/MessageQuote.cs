using System.Globalization;

namespace Backroom.Modules.Messaging.Application.Messages;

/// <summary>
/// What a reply shows of the message it quotes (docs/contracts/phase-2.md, flow 2). A live original: its id, author and
/// derived text cut to <see cref="MaxTextLength"/> characters. A deleted (or vanished) original: its id only and
/// <paramref name="Deleted"/>; nothing of its content or author, the tombstone's privacy (docs/adr/0011). The quote is
/// read from the original at read time, never copied, so an edit or a delete shows in every reply on its next read.
/// </summary>
internal sealed record MessageQuote(Guid MessageId, Guid? AuthorAccountId, string? Text, bool Deleted)
{
    /// <summary>The longest quoted text, in UTF-16 characters.</summary>
    public const int MaxTextLength = 200;

    public static MessageQuote OfDeleted(Guid messageId) => new(messageId, null, null, true);

    public static MessageQuote Of(Guid messageId, Guid authorAccountId, string text) =>
        new(messageId, authorAccountId, Truncate(text, MaxTextLength), false);

    /// <summary>At most <paramref name="maxLength"/> UTF-16 characters, cut back to a text element boundary (never inside a surrogate pair or a combining sequence).</summary>
    public static string Truncate(string text, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length <= maxLength)
        {
            return text;
        }

        var end = 0;
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            var next = enumerator.ElementIndex + ((string)enumerator.Current).Length;
            if (next > maxLength)
            {
                break;
            }

            end = next;
        }

        return text[..end];
    }
}
