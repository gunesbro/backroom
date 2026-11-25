using System.Globalization;
using Backroom.Modules.Messaging.Application.Conversations;
using Backroom.Modules.Messaging.Contracts;

namespace Backroom.Modules.Messaging.Application.Messages;

/// <summary>Previews for other modules: one query for the messages, one read check per distinct conversation.</summary>
internal sealed class MessagePreviews(IMessageStore messages, IConversationStore conversations) : IMessagePreviews
{
    public async Task<IReadOnlyDictionary<Guid, string>> ReadAsync(
        Guid organizationId, Guid accountId, IReadOnlyCollection<Guid> messageIds, int maxLength, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messageIds);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 1);
        var result = new Dictionary<Guid, string>();
        if (messageIds.Count == 0)
        {
            return result;
        }

        ArgumentOutOfRangeException.ThrowIfGreaterThan(messageIds.Count, IMessagePreviews.MaxMessages);
        var found = await messages.ListForPreviewAsync(messageIds, cancellationToken);
        var canRead = new Dictionary<Guid, bool>();
        foreach (var message in found)
        {
            if (!canRead.TryGetValue(message.ConversationId, out var allowed))
            {
                allowed = await conversations.FindAsync(organizationId, message.ConversationId, accountId, cancellationToken) is { CanRead: true };
                canRead[message.ConversationId] = allowed;
            }

            if (allowed)
            {
                result[message.Id] = Truncate(message.Text, maxLength);
            }
        }

        return result;
    }

    /// <summary>At most <paramref name="maxLength"/> UTF-16 characters, cut back to a text element boundary (never inside a surrogate pair or a combining sequence).</summary>
    private static string Truncate(string text, int maxLength)
    {
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
