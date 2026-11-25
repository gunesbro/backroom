using Microsoft.Extensions.Logging;
using Backroom.BuildingBlocks.Application.Identifiers;
using Backroom.BuildingBlocks.Application.Messaging;
using Backroom.BuildingBlocks.Application.Organizations;
using Backroom.BuildingBlocks.Domain.Errors;
using Backroom.Modules.Messaging.Application.Conversations;
using Backroom.Modules.Messaging.Application.Live;
using Backroom.Modules.Messaging.Contracts;
using Backroom.Modules.Permissions.Contracts;
using Backroom.Modules.Realtime.Contracts;

namespace Backroom.Modules.Messaging.Application.Channels;

/// <summary>
/// Channels of the entered organization (docs/contracts/phase-1.md, flow 4, step 1; docs/contracts/phase-2.md, flow 1).
/// The endpoint has checked the organization membership. Creating needs <c>channels.create-public</c> or, for a
/// private channel, <c>channels.create-private</c>. The creator becomes the first member (a private channel's owner);
/// the conversation and the memberships are announced through the outbox with ids only. A private channel is
/// announced live to its members alone, never to the organization.
/// </summary>
internal sealed partial class ChannelService(
    IChannelStore channels,
    IConversationStore conversations,
    MembershipService memberships,
    IOrganizationPermissionCheck permissions,
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

    public static readonly Error DescriptionInvalid =
        Error.Validation("messaging.channel_description_invalid", "The description must be at most 250 characters without control characters.");

    public static readonly Error Forbidden =
        Error.Forbidden("permissions.forbidden", "You do not have permission to do this.");

    public static readonly Error PageInvalid =
        Error.Validation("messaging.page_invalid", "The page size must be between 1 and 100.");

    public async Task<Result<ChannelEntry>> CreateAsync(
        Guid accountId, string? name, bool isPrivate, string? description, IReadOnlyList<Guid>? memberAccountIds, CancellationToken cancellationToken)
    {
        var organizationId = OrganizationId;
        if (!ChannelName.TryNormalize(name, out var normalized))
        {
            return NameInvalid;
        }

        if (!ChannelDescription.TryNormalize(description, out var normalizedDescription))
        {
            return DescriptionInvalid;
        }

        // The creator is always a member; naming them again changes nothing.
        if (!AccountIdList.TryDistinct(memberAccountIds, MembershipService.MaxAddedMembers, accountId, out var others))
        {
            return MembershipService.MembersInvalid;
        }

        if (!await permissions.HasPermissionAsync(accountId, isPrivate ? SystemRoles.ChannelsCreatePrivate : SystemRoles.ChannelsCreatePublic, cancellationToken))
        {
            return Forbidden;
        }

        var now = StoredTime.Now(timeProvider);
        if ((await ConversationBrakes.CheckNewChannelAsync(conversations, organizationId, accountId, now, cancellationToken)
            ?? await ConversationBrakes.CheckMemberAdditionsAsync(conversations, organizationId, accountId, others.Count, now, cancellationToken)) is { } limited)
        {
            return limited;
        }

        var kind = isPrivate ? ConversationKinds.PrivateChannel : ConversationKinds.PublicChannel;
        var channel = new NewChannel(
            ids.NewId(), organizationId, kind, normalized, ChannelName.KeyOf(normalized), normalizedDescription, isPrivate ? accountId : null, accountId, now);

        // Compared with open public channels only: a private channel's name is never compared and never decides the answer.
        if (!await channels.AddAsync(channel, cancellationToken))
        {
            return NameTaken;
        }

        // A new channel has no messages yet, so everyone joins at sequence number 0.
        var joined = await memberships.OpenAsync(
            organizationId, channel.Id, accountId, isPrivate ? MemberRoles.Owner : MemberRoles.Member, 0, accountId, cancellationToken);
        if (joined.Error is { } error)
        {
            // The unit of work rolls the channel back with the error.
            return error;
        }

        var added = await memberships.OpenManyAsync(organizationId, channel.Id, others, 0, accountId, now, cancellationToken);
        if (added.Error is { } refused)
        {
            return refused;
        }

        await outbox.AddAsync(
            new ConversationCreated(ids.NewId(), organizationId, now, channel.Id, kind, accountId),
            cancellationToken);
        if (!isPrivate)
        {
            // To the organization: public channels only. A private channel reaches its members through MembershipChanged.
            liveEvents.Add(new ChannelCreatedPayload(organizationId, new LiveChannel(channel.Id, channel.Name, now)));
        }

        LogCreated(logger, channel.Id, organizationId, accountId);
        return new ChannelEntry(channel.Id, kind, channel.Name, channel.Description, now);
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
