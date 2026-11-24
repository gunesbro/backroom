using Microsoft.Extensions.Logging;
using Backroom.BuildingBlocks.Application.Identifiers;
using Backroom.BuildingBlocks.Application.Messaging;
using Backroom.BuildingBlocks.Application.Organizations;
using Backroom.BuildingBlocks.Domain.Errors;
using Backroom.Modules.Messaging.Application.Conversations;
using Backroom.Modules.Messaging.Application.Live;
using Backroom.Modules.Messaging.Contracts;
using Backroom.Modules.Realtime.Contracts;

namespace Backroom.Modules.Messaging.Application.Channels;

/// <summary>
/// Public channels of the entered organization (docs/contracts/phase-1.md, flow 4, step 1; docs/contracts/phase-2.md,
/// flow 1). The endpoint has checked the membership, and <c>channels.create-public</c> for creating. The creator becomes
/// the channel's first member; the conversation and the membership are announced through the outbox.
/// </summary>
internal sealed partial class ChannelService(
    IChannelStore channels,
    MembershipService memberships,
    IOutbox outbox,
    AfterCommitLiveEvents liveEvents,
    IIdGenerator ids,
    IOrganizationContext context,
    TimeProvider timeProvider,
    ILogger<ChannelService> logger)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;

    public static readonly Error NameInvalid =
        Error.Validation("messaging.channel_name_invalid", "The channel name must be 1 to 80 characters without control characters.");

    public static readonly Error NameTaken =
        Error.Conflict("messaging.channel_name_taken", "A public channel with this name already exists.");

    public static readonly Error PageInvalid =
        Error.Validation("messaging.page_invalid", "The page size must be between 1 and 100.");

    public async Task<Result<ChannelEntry>> CreateAsync(Guid accountId, string? name, CancellationToken cancellationToken)
    {
        var organizationId = OrganizationId;
        if (!ChannelName.TryNormalize(name, out var normalized))
        {
            return NameInvalid;
        }

        var now = StoredTime.Now(timeProvider);
        var channel = new NewChannel(ids.NewId(), organizationId, normalized, ChannelName.KeyOf(normalized), accountId, now);

        // Compared with open public channels only: a private channel's name never decides the answer.
        if (!await channels.AddAsync(channel, cancellationToken))
        {
            return NameTaken;
        }

        // A new channel has no messages yet, so the creator joins at sequence number 0.
        var joined = await memberships.OpenAsync(organizationId, channel.Id, accountId, MemberRoles.Member, 0, accountId, cancellationToken);
        if (joined.Error is { } error)
        {
            // The unit of work rolls the channel back with the error.
            return error;
        }

        await outbox.AddAsync(
            new ConversationCreated(ids.NewId(), organizationId, now, channel.Id, ConversationKinds.PublicChannel, accountId),
            cancellationToken);
        liveEvents.Add(new ChannelCreatedPayload(organizationId, new LiveChannel(channel.Id, channel.Name, now)));
        LogCreated(logger, channel.Id, organizationId, accountId);
        return new ChannelEntry(channel.Id, channel.Name, now);
    }

    public async Task<Result<ChannelPage>> ListAsync(Guid? after, int? limit, CancellationToken cancellationToken)
    {
        var pageSize = limit ?? DefaultPageSize;
        if (pageSize is < 1 or > MaxPageSize)
        {
            return PageInvalid;
        }

        // One more than asked for tells whether another page follows.
        var found = await channels.ListAsync(OrganizationId, after ?? Guid.Empty, pageSize + 1, cancellationToken);
        return found.Count > pageSize
            ? new ChannelPage([.. found.Take(pageSize)], found[pageSize - 1].Id)
            : new ChannelPage(found, null);
    }

    private Guid OrganizationId =>
        context.OrganizationId ?? throw new InvalidOperationException("The request has not entered an organization.");

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Channel {ChannelId} created in organization {OrganizationId} by account {AccountId}.")]
    private static partial void LogCreated(ILogger logger, Guid channelId, Guid organizationId, Guid accountId);
}
