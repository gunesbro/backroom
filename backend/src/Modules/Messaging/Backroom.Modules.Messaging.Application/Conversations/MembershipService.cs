using Microsoft.Extensions.Logging;
using Backroom.BuildingBlocks.Application.Identifiers;
using Backroom.BuildingBlocks.Application.Messaging;
using Backroom.BuildingBlocks.Application.Organizations;
using Backroom.BuildingBlocks.Domain.Errors;
using Backroom.Modules.Messaging.Application.Live;
using Backroom.Modules.Messaging.Contracts;
using Backroom.Modules.Realtime.Contracts;

namespace Backroom.Modules.Messaging.Application.Conversations;

/// <summary>
/// Membership of conversations (docs/contracts/phase-2.md, flow 1; HTTP <c>.../conversations/{id}/join</c>,
/// <c>/leave</c>, <c>/members</c> and <c>/owner</c>). The endpoint has checked the organization membership. Every
/// change locks the conversation row first, so changes to one conversation run one at a time, and writes its
/// integration events to the outbox in the same unit of work. A conversation the account may not know of answers
/// <see cref="ConversationNotFound"/>, the same as an unknown one or one of another organization, whatever the
/// account's role in the organization.
/// </summary>
internal sealed partial class MembershipService(
    IConversationStore conversations,
    IOrganizationMembershipCheck organizationMembers,
    MemberRemoval removal,
    IOutbox outbox,
    AfterCommitLiveEvents liveEvents,
    IIdGenerator ids,
    IOrganizationContext context,
    TimeProvider timeProvider,
    ILogger<MembershipService> logger)
{
    /// <summary>Open memberships per person and organization (docs/contracts/phase-2.md, "Data").</summary>
    public const int MaxOpenMemberships = 10_000;

    /// <summary>People per call when adding members or creating a channel (flow 1).</summary>
    public const int MaxAddedMembers = 50;

    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;

    public static readonly Error ConversationNotFound =
        Error.NotFound("messaging.conversation_not_found", "The conversation was not found.");

    public static readonly Error ConversationArchived =
        Error.Conflict("messaging.conversation_archived", "The conversation is archived.");

    public static readonly Error CannotLeaveDm =
        Error.Conflict("messaging.cannot_leave_dm", "A direct message cannot be left.");

    public static readonly Error MembershipLimit =
        Error.Conflict("messaging.membership_limit", "A person cannot be a member of that many conversations.");

    public static readonly Error MembersInvalid =
        Error.Validation("messaging.members_invalid", "Name 1 to 50 accounts.");

    public static readonly Error MemberNotInOrganization =
        Error.Validation("messaging.member_not_in_organization", "Every account must be an active member of the organization.");

    public static readonly Error GroupDmAddCreatesNew =
        Error.Conflict("messaging.group_dm_add_creates_new", "Adding a person to a group conversation creates a new group.");

    public static readonly Error NotAPrivateChannel =
        Error.Conflict("messaging.not_a_private_channel", "Only a private channel has an owner who manages its members.");

    public static readonly Error NotOwner =
        Error.Forbidden("messaging.not_conversation_owner", "Only the channel's owner can do this.");

    public static readonly Error RemovalUnavailable =
        Error.Forbidden("messaging.member_removal_unavailable", "Removing members is not available yet.");

    public static readonly Error OwnerMustBeMember =
        Error.Conflict("messaging.owner_must_be_member", "The new owner must be a member of the channel.");

    public static readonly Error PageInvalid =
        Error.Validation("messaging.page_invalid", "The page size must be between 1 and 100.");

    /// <summary>
    /// Joins a public channel. Joining one the account is already in succeeds without a change (also for a private
    /// conversation it belongs to); every other conversation it cannot read is not found.
    /// </summary>
    public async Task<Result> JoinAsync(Guid accountId, Guid conversationId, CancellationToken cancellationToken)
    {
        var organizationId = OrganizationId;
        var conversation = await conversations.GetAsync(organizationId, conversationId, accountId, forUpdate: true, cancellationToken);
        if (conversation is not { IsVisible: true })
        {
            return ConversationNotFound;
        }

        if (conversation.IsMember)
        {
            return Result.Success;
        }

        if (conversation.Archived)
        {
            return ConversationArchived;
        }

        return await OpenAsync(organizationId, conversationId, accountId, MemberRoles.Member, conversation.ChangeSeq, accountId, cancellationToken);
    }

    /// <summary>
    /// Leaves a conversation; always allowed except for a DM. Leaving a readable conversation the account is not in
    /// succeeds without a change. Read access ends with the commit. When a private channel's owner leaves, ownership
    /// passes to the member who joined earliest (ties: the lowest account id); a private channel its last member
    /// leaves is archived, because nobody can reach it any more.
    /// </summary>
    public async Task<Result> LeaveAsync(Guid accountId, Guid conversationId, CancellationToken cancellationToken)
    {
        var organizationId = OrganizationId;
        var conversation = await conversations.GetAsync(organizationId, conversationId, accountId, forUpdate: true, cancellationToken);
        if (conversation is not { IsVisible: true })
        {
            return ConversationNotFound;
        }

        if (string.Equals(conversation.Kind, ConversationKinds.Dm, StringComparison.Ordinal))
        {
            return CannotLeaveDm;
        }

        var now = StoredTime.Now(timeProvider);
        if (!await CloseAsync(organizationId, conversationId, accountId, accountId, now, cancellationToken))
        {
            return Result.Success;
        }

        LogLeft(logger, accountId, conversationId, organizationId);
        if (conversation is { IsPrivateChannel: true, IsOwner: true })
        {
            await PassOwnershipAsync(organizationId, conversation, accountId, now, cancellationToken);
        }

        return Result.Success;
    }

    /// <summary>
    /// Adds active members of the organization to a private channel; any member of the channel may (flow 1). People
    /// who are already in it are skipped.
    /// </summary>
    public async Task<Result> AddAsync(Guid accountId, Guid conversationId, IReadOnlyList<Guid>? accountIds, CancellationToken cancellationToken)
    {
        if (!AccountIdList.TryDistinct(accountIds, MaxAddedMembers, exclude: null, out var added) || added.Count == 0)
        {
            return MembersInvalid;
        }

        var organizationId = OrganizationId;
        var conversation = await conversations.GetAsync(organizationId, conversationId, accountId, forUpdate: true, cancellationToken);
        if (conversation is not { IsVisible: true })
        {
            return ConversationNotFound;
        }

        if (string.Equals(conversation.Kind, ConversationKinds.GroupDm, StringComparison.Ordinal))
        {
            return GroupDmAddCreatesNew;
        }

        if (!conversation.IsPrivateChannel)
        {
            return NotAPrivateChannel;
        }

        if (conversation.Archived)
        {
            return ConversationArchived;
        }

        var now = StoredTime.Now(timeProvider);
        if (await ConversationBrakes.CheckMemberAdditionsAsync(conversations, organizationId, accountId, added.Count, now, cancellationToken) is { } limited)
        {
            return limited;
        }

        var opened = await OpenManyAsync(organizationId, conversationId, added, conversation.ChangeSeq, accountId, now, cancellationToken);
        if (opened.Error is { } error)
        {
            return error;
        }

        var count = opened.GetValueOrThrow().Count;
        LogAdded(logger, count, conversationId, organizationId, accountId);
        return Result.Success;
    }

    /// <summary>
    /// Removes a member from a private channel; only its owner may, and removing oneself is leaving. Removing someone
    /// who is not a member succeeds without a change.
    /// </summary>
    public async Task<Result> RemoveAsync(Guid accountId, Guid conversationId, Guid removedAccountId, CancellationToken cancellationToken)
    {
        if (removedAccountId == accountId)
        {
            return await LeaveAsync(accountId, conversationId, cancellationToken);
        }

        var organizationId = OrganizationId;
        var conversation = await conversations.GetAsync(organizationId, conversationId, accountId, forUpdate: true, cancellationToken);
        if (conversation is not { IsVisible: true })
        {
            return ConversationNotFound;
        }

        if (!conversation.IsPrivateChannel)
        {
            return NotAPrivateChannel;
        }

        if (!conversation.IsOwner)
        {
            return NotOwner;
        }

        if (!removal.Enabled)
        {
            return RemovalUnavailable;
        }

        var now = StoredTime.Now(timeProvider);
        if (await CloseAsync(organizationId, conversationId, removedAccountId, accountId, now, cancellationToken))
        {
            LogRemoved(logger, removedAccountId, conversationId, organizationId, accountId);
        }

        return Result.Success;
    }

    /// <summary>Hands a private channel to another of its members; only the owner may.</summary>
    public async Task<Result> TransferOwnershipAsync(Guid accountId, Guid conversationId, Guid? ownerAccountId, CancellationToken cancellationToken)
    {
        if (ownerAccountId is not { } newOwner || newOwner == Guid.Empty)
        {
            return MembersInvalid;
        }

        var organizationId = OrganizationId;
        var conversation = await conversations.GetAsync(organizationId, conversationId, accountId, forUpdate: true, cancellationToken);
        if (conversation is not { IsVisible: true })
        {
            return ConversationNotFound;
        }

        if (!conversation.IsPrivateChannel)
        {
            return NotAPrivateChannel;
        }

        if (!conversation.IsOwner)
        {
            return NotOwner;
        }

        if (newOwner == accountId)
        {
            return Result.Success;
        }

        // Under the conversation row lock, so the membership cannot end before the ownership is written.
        if (await conversations.FindAsync(organizationId, conversationId, newOwner, cancellationToken) is not { IsMember: true })
        {
            return OwnerMustBeMember;
        }

        await conversations.SetOwnerAsync(organizationId, conversationId, accountId, newOwner, cancellationToken);
        await outbox.AddAsync(
            new ConversationChanged(
                ids.NewId(), organizationId, StoredTime.Now(timeProvider), conversationId, conversation.Kind, ConversationChanges.OwnerTransferred, accountId, conversation.MemberCount),
            cancellationToken);
        LogOwnerChanged(logger, conversationId, organizationId, newOwner);
        return Result.Success;
    }

    /// <summary>The open members of a conversation the account may know of, by account id.</summary>
    public async Task<Result<ConversationMemberPage>> ListAsync(Guid accountId, Guid conversationId, Guid? after, int? limit, CancellationToken cancellationToken)
    {
        var pageSize = limit ?? DefaultPageSize;
        if (pageSize is < 1 or > MaxPageSize)
        {
            return PageInvalid;
        }

        var organizationId = OrganizationId;
        if (await conversations.GetAsync(organizationId, conversationId, accountId, forUpdate: false, cancellationToken) is not { IsVisible: true })
        {
            return ConversationNotFound;
        }

        // One more than asked for tells whether another page follows.
        var found = await conversations.ListMembersAsync(organizationId, conversationId, after ?? Guid.Empty, pageSize + 1, cancellationToken);
        return found.Count > pageSize
            ? new ConversationMemberPage([.. found.Take(pageSize)], found[pageSize - 1].AccountId)
            : new ConversationMemberPage(found, null);
    }

    /// <summary>
    /// Opens a membership and writes <see cref="ConversationMemberAdded"/>, within the limit of open memberships.
    /// Concurrent joins of one person to different conversations can pass the count together, so the limit can be
    /// exceeded by that many; it bounds the sidebar and the per-person queries, which tolerate that.
    /// </summary>
    internal async Task<Result> OpenAsync(
        Guid organizationId, Guid conversationId, Guid accountId, string role, long joinedSeq, Guid addedBy, CancellationToken cancellationToken)
    {
        if (await conversations.CountOpenMembershipsAsync(organizationId, accountId, MaxOpenMemberships, cancellationToken) >= MaxOpenMemberships)
        {
            return MembershipLimit;
        }

        var now = StoredTime.Now(timeProvider);
        if (!await conversations.OpenMembershipAsync(
            new NewMembership(organizationId, conversationId, accountId, role, now, joinedSeq, addedBy), cancellationToken))
        {
            // Already a member.
            return Result.Success;
        }

        await AnnounceAddedAsync(organizationId, conversationId, accountId, addedBy, now, cancellationToken);
        LogJoined(logger, accountId, conversationId, organizationId);
        return Result.Success;
    }

    /// <summary>
    /// Opens memberships for several people at once (at most <see cref="MaxAddedMembers"/>): every one must be an
    /// active member of the organization and below the limit of open memberships, else nothing is opened. Returns the
    /// accounts that were not members before.
    /// </summary>
    internal async Task<Result<IReadOnlyList<Guid>>> OpenManyAsync(
        Guid organizationId, Guid conversationId, IReadOnlyList<Guid> accountIds, long joinedSeq, Guid addedBy, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (accountIds.Count == 0)
        {
            return Result<IReadOnlyList<Guid>>.Success([]);
        }

        if ((await organizationMembers.SelectActiveMembersAsync(accountIds, cancellationToken)).Count != accountIds.Count)
        {
            return MemberNotInOrganization;
        }

        if (await conversations.AnyAtMembershipLimitAsync(organizationId, accountIds, MaxOpenMemberships, cancellationToken))
        {
            return MembershipLimit;
        }

        var opened = await conversations.OpenMembershipsAsync(organizationId, conversationId, accountIds, now, joinedSeq, addedBy, cancellationToken);
        foreach (var accountId in opened)
        {
            await AnnounceAddedAsync(organizationId, conversationId, accountId, addedBy, now, cancellationToken);
        }

        return Result<IReadOnlyList<Guid>>.Success(opened);
    }

    private async Task AnnounceAddedAsync(Guid organizationId, Guid conversationId, Guid accountId, Guid addedBy, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await outbox.AddAsync(
            new ConversationMemberAdded(ids.NewId(), organizationId, now, conversationId, accountId, addedBy),
            cancellationToken);
        liveEvents.Add(new MembershipChangedPayload(organizationId, accountId, conversationId, MembershipChangedPayload.Added));
    }

    // The removal from the live group goes first after the commit, and again through the outbox event.
    private async Task<bool> CloseAsync(Guid organizationId, Guid conversationId, Guid accountId, Guid actorAccountId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!await conversations.CloseMembershipAsync(organizationId, conversationId, accountId, now, cancellationToken))
        {
            return false;
        }

        await outbox.AddAsync(
            new ConversationMemberRemoved(ids.NewId(), organizationId, now, conversationId, accountId, actorAccountId),
            cancellationToken);
        liveEvents.AddRemoval(organizationId, conversationId, accountId);
        liveEvents.Add(new MembershipChangedPayload(organizationId, accountId, conversationId, MembershipChangedPayload.Removed));
        return true;
    }

    private async Task PassOwnershipAsync(Guid organizationId, ConversationDetails conversation, Guid leftAccountId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var remaining = conversation.MemberCount - 1;
        if (await conversations.EarliestMemberAsync(organizationId, conversation.Id, cancellationToken) is { } successor)
        {
            await conversations.SetOwnerAsync(organizationId, conversation.Id, leftAccountId, successor, cancellationToken);
            await outbox.AddAsync(
                new ConversationChanged(ids.NewId(), organizationId, now, conversation.Id, conversation.Kind, ConversationChanges.OwnerTransferred, leftAccountId, remaining),
                cancellationToken);
            LogOwnerChanged(logger, conversation.Id, organizationId, successor);
            return;
        }

        if (!conversation.Archived)
        {
            // Nobody is left to tell: no live event.
            await conversations.SetArchivedAsync(organizationId, conversation.Id, now, cancellationToken);
            await outbox.AddAsync(
                new ConversationChanged(ids.NewId(), organizationId, now, conversation.Id, conversation.Kind, ConversationChanges.Archived, leftAccountId, 0),
                cancellationToken);
        }
    }

    private Guid OrganizationId =>
        context.OrganizationId ?? throw new InvalidOperationException("The request has not entered an organization.");

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Account {AccountId} joined conversation {ConversationId} of organization {OrganizationId}.")]
    private static partial void LogJoined(ILogger logger, Guid accountId, Guid conversationId, Guid organizationId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Account {AccountId} left conversation {ConversationId} of organization {OrganizationId}.")]
    private static partial void LogLeft(ILogger logger, Guid accountId, Guid conversationId, Guid organizationId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "{Count} accounts added to conversation {ConversationId} of organization {OrganizationId} by account {ActorAccountId}.")]
    private static partial void LogAdded(ILogger logger, int count, Guid conversationId, Guid organizationId, Guid actorAccountId);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Account {AccountId} removed from conversation {ConversationId} of organization {OrganizationId} by account {ActorAccountId}.")]
    private static partial void LogRemoved(ILogger logger, Guid accountId, Guid conversationId, Guid organizationId, Guid actorAccountId);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information, Message = "Conversation {ConversationId} of organization {OrganizationId} is now owned by account {AccountId}.")]
    private static partial void LogOwnerChanged(ILogger logger, Guid conversationId, Guid organizationId, Guid accountId);
}
