using Microsoft.Extensions.Logging;
using Backroom.BuildingBlocks.Application.Identifiers;
using Backroom.BuildingBlocks.Application.Messaging;
using Backroom.BuildingBlocks.Application.Organizations;
using Backroom.BuildingBlocks.Domain.Errors;
using Backroom.Modules.Messaging.Contracts;

namespace Backroom.Modules.Messaging.Application.Conversations;

/// <summary>
/// Joining and leaving conversations (docs/contracts/phase-2.md, flow 1; HTTP <c>POST .../conversations/{id}/join</c>
/// and <c>/leave</c>). The endpoint has checked the organization membership. Every change writes its integration event
/// to the outbox in the same unit of work. A conversation the account cannot read answers
/// <see cref="ConversationNotFound"/>, the same as an unknown one or one of another organization.
/// </summary>
internal sealed partial class MembershipService(
    IConversationStore conversations,
    IOutbox outbox,
    IIdGenerator ids,
    IOrganizationContext context,
    TimeProvider timeProvider,
    ILogger<MembershipService> logger)
{
    /// <summary>Open memberships per person and organization (docs/contracts/phase-2.md, "Data").</summary>
    public const int MaxOpenMemberships = 10_000;

    public static readonly Error ConversationNotFound =
        Error.NotFound("messaging.conversation_not_found", "The conversation was not found.");

    public static readonly Error ConversationArchived =
        Error.Conflict("messaging.conversation_archived", "The conversation is archived.");

    public static readonly Error CannotLeaveDm =
        Error.Conflict("messaging.cannot_leave_dm", "A direct message cannot be left.");

    public static readonly Error MembershipLimit =
        Error.Conflict("messaging.membership_limit", "You are a member of too many conversations.");

    /// <summary>
    /// Joins a public channel. Joining one the account is already in succeeds without a change (also for a private
    /// conversation it belongs to); every other conversation it cannot read is not found.
    /// </summary>
    public async Task<Result> JoinAsync(Guid accountId, Guid conversationId, CancellationToken cancellationToken)
    {
        var organizationId = OrganizationId;
        var conversation = await conversations.FindAsync(organizationId, conversationId, accountId, cancellationToken);
        if (conversation is null || !(conversation.IsPublicChannel || conversation.IsMember))
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

        return await OpenAsync(organizationId, conversationId, accountId, MemberRoles.Member, conversation.LastSeq, accountId, cancellationToken);
    }

    /// <summary>
    /// Leaves a conversation; always allowed except for a DM. Leaving a readable conversation the account is not in
    /// succeeds without a change. Read access ends with the commit.
    /// </summary>
    public async Task<Result> LeaveAsync(Guid accountId, Guid conversationId, CancellationToken cancellationToken)
    {
        var organizationId = OrganizationId;
        var conversation = await conversations.FindAsync(organizationId, conversationId, accountId, cancellationToken);
        if (conversation is null || !(conversation.IsPublicChannel || conversation.IsMember))
        {
            return ConversationNotFound;
        }

        if (string.Equals(conversation.Kind, ConversationKinds.Dm, StringComparison.Ordinal))
        {
            return CannotLeaveDm;
        }

        var now = StoredTime.Now(timeProvider);
        if (!await conversations.CloseMembershipAsync(organizationId, conversationId, accountId, now, cancellationToken))
        {
            return Result.Success;
        }

        await outbox.AddAsync(
            new ConversationMemberRemoved(ids.NewId(), organizationId, now, conversationId, accountId, accountId),
            cancellationToken);
        LogLeft(logger, accountId, conversationId, organizationId);
        return Result.Success;
    }

    /// <summary>
    /// Opens a membership and writes <see cref="ConversationMemberAdded"/>, within the limit of open memberships.
    /// Concurrent joins of one person can pass the count together, so the limit can be exceeded by that many; it
    /// bounds the sidebar and the per-person queries, which tolerate that.
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
            // A concurrent join of the same person committed first.
            return Result.Success;
        }

        await outbox.AddAsync(
            new ConversationMemberAdded(ids.NewId(), organizationId, now, conversationId, accountId, addedBy),
            cancellationToken);
        LogJoined(logger, accountId, conversationId, organizationId);
        return Result.Success;
    }

    private Guid OrganizationId =>
        context.OrganizationId ?? throw new InvalidOperationException("The request has not entered an organization.");

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Account {AccountId} joined conversation {ConversationId} of organization {OrganizationId}.")]
    private static partial void LogJoined(ILogger logger, Guid accountId, Guid conversationId, Guid organizationId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Account {AccountId} left conversation {ConversationId} of organization {OrganizationId}.")]
    private static partial void LogLeft(ILogger logger, Guid accountId, Guid conversationId, Guid organizationId);
}
