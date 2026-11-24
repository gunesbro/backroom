using Microsoft.Extensions.Logging;
using Backroom.BuildingBlocks.Application.Identifiers;
using Backroom.BuildingBlocks.Application.Messaging;
using Backroom.BuildingBlocks.Application.Organizations;
using Backroom.BuildingBlocks.Domain.Errors;
using Backroom.Modules.Messaging.Application.Channels;
using Backroom.Modules.Messaging.Application.Live;
using Backroom.Modules.Messaging.Contracts;
using Backroom.Modules.Realtime.Contracts;

namespace Backroom.Modules.Messaging.Application.Messages;

/// <summary>
/// Plain-text messages in public channels (docs/contracts/phase-1.md, flow 4, steps 2 and 3). The endpoint has checked
/// the membership; Phase 1 channels are public, so every active member may read and write every open channel. The
/// message and its outbox row are written in the request's unit of work; the live event is held until it commits.
/// </summary>
internal sealed partial class MessageService(
    IMessageStore messages,
    IChannelStore channels,
    IOutbox outbox,
    AfterCommitLiveEvents liveEvents,
    IIdGenerator ids,
    IOrganizationContext context,
    TimeProvider timeProvider,
    ILogger<MessageService> logger)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;

    /// <summary>The largest <c>before</c> there is: a UUID compares bytewise, so no message id is above it.</summary>
    private static readonly Guid s_newest = new("ffffffff-ffff-ffff-ffff-ffffffffffff");

    public static readonly Error ChannelNotFound =
        Error.NotFound("messaging.channel_not_found", "The channel was not found.");

    public static readonly Error MessageInvalid =
        Error.Validation("messaging.message_invalid", "The message needs a client message id and 1 to 4000 characters of text without control characters.");

    public static readonly Error PageInvalid =
        Error.Validation("messaging.page_invalid", "Use either before or after, and a page size between 1 and 100.");

    public async Task<Result<SentMessage>> SendAsync(Guid authorAccountId, Guid channelId, Guid? clientMessageId, string? text, CancellationToken cancellationToken)
    {
        var organizationId = OrganizationId;
        if (clientMessageId is not { } clientId || clientId == Guid.Empty || !MessageText.TryPrepare(text, out var stored))
        {
            return MessageInvalid;
        }

        if (!await channels.ExistsAsync(channelId, cancellationToken))
        {
            return ChannelNotFound;
        }

        var now = StoredTime.Now(timeProvider);
        var message = new NewMessage(ids.NewId(), organizationId, channelId, authorAccountId, clientId, stored, MessageText.ToBlocks(stored), now);
        if (!await messages.AddAsync(message, cancellationToken))
        {
            // A repeat (a retry after a lost answer): the first one stays, nothing is published again.
            var existing = await messages.FindAsync(channelId, authorAccountId, clientId, cancellationToken)
                ?? throw new InvalidOperationException("The message exists by its unique index but could not be read.");
            return new SentMessage(existing, Created: false);
        }

        await outbox.AddAsync(
            new MessagePosted(ids.NewId(), organizationId, now, channelId, message.Id, authorAccountId, now),
            cancellationToken);
        liveEvents.Add(new MessagePostedPayload(organizationId, channelId, new LiveMessage(message.Id, clientId, authorAccountId, stored, now)));
        LogPosted(logger, message.Id, channelId, organizationId, authorAccountId);
        return new SentMessage(new MessageEntry(message.Id, clientId, authorAccountId, stored, now), Created: true);
    }

    public async Task<Result<MessagePage>> ListAsync(Guid channelId, Guid? before, Guid? after, int? limit, CancellationToken cancellationToken)
    {
        var pageSize = limit ?? DefaultPageSize;
        if (pageSize is < 1 or > MaxPageSize || (before is not null && after is not null))
        {
            return PageInvalid;
        }

        if (!await channels.ExistsAsync(channelId, cancellationToken))
        {
            return ChannelNotFound;
        }

        // One more than asked for tells whether another page follows.
        var found = after is { } cursor
            ? await messages.ListAfterAsync(channelId, cursor, pageSize + 1, cancellationToken)
            : await messages.ListBeforeAsync(channelId, before ?? s_newest, pageSize + 1, cancellationToken);
        return found.Count > pageSize
            ? new MessagePage([.. found.Take(pageSize)], found[pageSize - 1].Id)
            : new MessagePage(found, null);
    }

    private Guid OrganizationId =>
        context.OrganizationId ?? throw new InvalidOperationException("The request has not entered an organization.");

    // Ids only: never the text.
    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Message {MessageId} posted in channel {ChannelId} of organization {OrganizationId} by account {AccountId}.")]
    private static partial void LogPosted(ILogger logger, Guid messageId, Guid channelId, Guid organizationId, Guid accountId);
}
