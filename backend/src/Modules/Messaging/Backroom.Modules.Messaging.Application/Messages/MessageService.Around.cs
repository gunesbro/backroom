using Backroom.BuildingBlocks.Domain.Errors;

namespace Backroom.Modules.Messaging.Application.Messages;

internal sealed partial class MessageService
{
    /// <summary>
    /// The message <paramref name="messageId"/> with up to <paramref name="limit"/> messages in all around it (docs/contracts/phase-2.md,
    /// "Jump to a message"). Read access is the same as for every read; an unknown message, one of another conversation and an
    /// unreadable conversation all answer <see cref="MessageNotFound"/>, so the answer is no oracle for any of them.
    /// </summary>
    public async Task<Result<MessageAround>> ListAroundAsync(
        Guid accountId, Guid conversationId, Guid messageId, string? epoch, long? before, long? after, long? changedAfter, int? limit, CancellationToken cancellationToken)
    {
        var pageSize = limit ?? DefaultPageSize;
        if (pageSize is < 1 or > MaxPageSize || before is not null || after is not null || changedAfter is not null)
        {
            return PageInvalid;
        }

        var access = await conversations.FindAsync(OrganizationId, conversationId, accountId, cancellationToken);
        if (access is not { CanRead: true })
        {
            return MessageNotFound;
        }

        if (await EpochAsync(cancellationToken) is not { } current)
        {
            return EpochUnconfirmed;
        }

        // The request carries no cursor, so no epoch is required, but one that is presented must be the current one.
        if (epoch is not null && (epoch.Length > MaxEpochLength || !string.Equals(epoch, current, StringComparison.Ordinal)))
        {
            return ResyncRequired;
        }

        var anchor = await messages.FindInConversationAsync(conversationId, messageId, cancellationToken);
        if (anchor is null)
        {
            return MessageNotFound;
        }

        // The anchor takes one place; the older side gets the smaller half. One more than a side can show tells whether it continues.
        var others = pageSize - 1;
        var olderWanted = others / 2;
        var older = await messages.ListBeforeAsync(conversationId, anchor.Seq, others + 1, cancellationToken);
        var newer = await messages.ListAfterAsync(conversationId, anchor.Seq, others + 1, cancellationToken);

        // A side that runs out hands its places to the other one.
        var takeOlder = Math.Min(older.Count, Math.Max(olderWanted, others - newer.Count));
        var takeNewer = Math.Min(newer.Count, others - takeOlder);

        var items = new List<MessageEntry>(takeOlder + takeNewer + 1);
        for (var index = takeNewer - 1; index >= 0; index--)
        {
            items.Add(newer[index]);
        }

        items.Add(anchor);
        for (var index = 0; index < takeOlder; index++)
        {
            items.Add(older[index]);
        }

        long? olderCursor = older.Count > takeOlder ? (takeOlder > 0 ? older[takeOlder - 1].Seq : anchor.Seq) : null;
        long? newerCursor = newer.Count > takeNewer ? (takeNewer > 0 ? newer[takeNewer - 1].Seq : anchor.Seq) : null;
        return new MessageAround(items, anchor, olderCursor, newerCursor, current);
    }
}
