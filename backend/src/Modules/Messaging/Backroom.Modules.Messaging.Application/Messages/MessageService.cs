using Microsoft.Extensions.Logging;
using Backroom.BuildingBlocks.Application.Identifiers;
using Backroom.BuildingBlocks.Application.Messaging;
using Backroom.BuildingBlocks.Application.Organizations;
using Backroom.BuildingBlocks.Domain.Errors;
using Backroom.Modules.Messaging.Application.Channels;
using Backroom.Modules.Messaging.Application.Conversations;
using Backroom.Modules.Messaging.Application.Live;
using Backroom.Modules.Messaging.Contracts;
using Backroom.Modules.Realtime.Contracts;

namespace Backroom.Modules.Messaging.Application.Messages;

/// <summary>
/// Plain-text messages (docs/contracts/phase-1.md, flow 4, steps 2 and 3). The endpoint has checked the organization
/// membership; reading and writing also need read access to the conversation (<see cref="ConversationAccess.CanRead"/>:
/// every active member for an open public channel, open members for every other kind), checked first, so nothing,
/// not even an idempotent repeat, answers for a conversation the account cannot read. Phase 1's routes keep posting
/// to public channels without joining them; the conversation routes (Phase 2 step 3) add that rule. The message and
/// its outbox row are written in the request unit of work; the live event is held until it commits.
/// </summary>
internal sealed partial class MessageService(
    IMessageStore messages,
    IChannelStore channels,
    IConversationStore conversations,
    IOutbox outbox,
    AfterCommitLiveEvents liveEvents,
    IIdGenerator ids,
    IOrganizationContext context,
    TimeProvider timeProvider,
    ILogger<MessageService> logger)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;

    public static readonly Error ChannelNotFound =
        Error.NotFound("messaging.channel_not_found", "The channel was not found.");

    public static readonly Error MessageInvalid =
        Error.Validation("messaging.message_invalid", "The message needs a client message id and 1 to 4000 characters of text without control characters.");

    public static readonly Error PageInvalid =
        Error.Validation("messaging.page_invalid", "Use either before or after, a cursor of 0 or more, and a page size between 1 and 100.");

    public async Task<Result<SentMessage>> SendAsync(Guid authorAccountId, Guid channelId, Guid? clientMessageId, string? text, CancellationToken cancellationToken)
    {
        var organizationId = OrganizationId;
        if (clientMessageId is not { } clientId || clientId == Guid.Empty || !MessageText.TryPrepare(text, out var prepared))
        {
            return MessageInvalid;
        }

        if (!await CanReadAsync(organizationId, channelId, authorAccountId, cancellationToken))
        {
            return ChannelNotFound;
        }

        // A repeat (a retry after a lost answer) is answered before any lock is taken. This and the unique index rely on
        // READ COMMITTED (the default, checked by a test): a concurrent repeat that waits for the first send to commit
        // then sees its row.
        if (await messages.FindAsync(channelId, authorAccountId, clientId, cancellationToken) is { } stored)
        {
            return new SentMessage(stored, Created: false);
        }

        // Takes the channel row lock until commit: sequence numbers follow commit order, which catch-up relies on.
        if (await channels.NextSequenceAsync(organizationId, channelId, cancellationToken) is not { } seq)
        {
            return ChannelNotFound;
        }

        var now = StoredTime.Now(timeProvider);
        var message = new NewMessage(ids.NewId(), organizationId, channelId, seq, authorAccountId, clientId, prepared, MessageText.ToBlocks(prepared), now);
        if (!await messages.AddAsync(message, cancellationToken))
        {
            // Another send of the same client message id committed between the check above and the lock; the first stays
            // (the sequence number taken here is left unused: sequences have gaps, clients never assume otherwise).
            var existing = await messages.FindAsync(channelId, authorAccountId, clientId, cancellationToken)
                ?? throw new InvalidOperationException("The message exists by its unique index but could not be read.");
            return new SentMessage(existing, Created: false);
        }

        await outbox.AddAsync(
            new MessagePosted(ids.NewId(), organizationId, now, channelId, message.Id, authorAccountId, now),
            cancellationToken);
        liveEvents.Add(new MessagePostedPayload(organizationId, channelId, new LiveMessage(message.Id, seq, clientId, authorAccountId, prepared, now)));
        LogPosted(logger, message.Id, channelId, organizationId, authorAccountId);
        return new SentMessage(new MessageEntry(message.Id, seq, clientId, authorAccountId, prepared, now), Created: true);
    }

    public async Task<Result<MessagePage>> ListAsync(Guid accountId, Guid channelId, long? before, long? after, int? limit, CancellationToken cancellationToken)
    {
        var pageSize = limit ?? DefaultPageSize;
        if (pageSize is < 1 or > MaxPageSize || (before is not null && after is not null) || before < 0 || after < 0)
        {
            return PageInvalid;
        }

        if (!await CanReadAsync(OrganizationId, channelId, accountId, cancellationToken))
        {
            return ChannelNotFound;
        }

        // One more than asked for tells whether another page follows.
        var found = after is { } cursor
            ? await messages.ListAfterAsync(channelId, cursor, pageSize + 1, cancellationToken)
            : await messages.ListBeforeAsync(channelId, before ?? long.MaxValue, pageSize + 1, cancellationToken);
        return found.Count > pageSize
            ? new MessagePage([.. found.Take(pageSize)], found[pageSize - 1].Seq)
            : new MessagePage(found, null);
    }

    private async Task<bool> CanReadAsync(Guid organizationId, Guid conversationId, Guid accountId, CancellationToken cancellationToken) =>
        await conversations.FindAsync(organizationId, conversationId, accountId, cancellationToken) is { CanRead: true };

    private Guid OrganizationId =>
        context.OrganizationId ?? throw new InvalidOperationException("The request has not entered an organization.");

    // Ids only: never the text.
    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Message {MessageId} posted in channel {ChannelId} of organization {OrganizationId} by account {AccountId}.")]
    private static partial void LogPosted(ILogger logger, Guid messageId, Guid channelId, Guid organizationId, Guid accountId);
}
