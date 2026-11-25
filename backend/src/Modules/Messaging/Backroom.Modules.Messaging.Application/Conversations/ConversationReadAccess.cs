using Backroom.BuildingBlocks.Application.Organizations;
using Backroom.Modules.Messaging.Contracts;

namespace Backroom.Modules.Messaging.Application.Conversations;

/// <summary>Read access and membership for other modules (<see cref="ConversationAccess"/>); primary-key probes, or one statement for a batch.</summary>
internal sealed class ConversationReadAccess(IConversationStore conversations, IOrganizationMembershipCheck members) : IConversationReadAccess
{
    public async Task<bool> CanReadAsync(Guid organizationId, Guid conversationId, Guid accountId, CancellationToken cancellationToken) =>
        await conversations.FindAsync(organizationId, conversationId, accountId, cancellationToken) is { CanRead: true };

    public async Task<bool> IsMemberAsync(Guid organizationId, Guid conversationId, Guid accountId, CancellationToken cancellationToken) =>
        await conversations.FindAsync(organizationId, conversationId, accountId, cancellationToken) is { IsMember: true };

    public async Task<IReadOnlyList<Guid>> FilterReadersAsync(
        Guid organizationId, Guid conversationId, IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(accountIds);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(accountIds.Count, IConversationReadAccess.MaxBatch);
        if (accountIds.Count == 0)
        {
            return [];
        }

        // Active organization members first (a removed member or a guest is not one), then the conversation's read rule.
        var active = await members.SelectActiveMembersAsync(accountIds, cancellationToken);
        return active.Count == 0 ? [] : await conversations.FilterReadersAsync(organizationId, conversationId, active, cancellationToken);
    }

    public async Task<IReadOnlySet<Guid>> ReadableConversationsAsync(
        Guid organizationId, Guid accountId, IReadOnlyCollection<Guid> conversationIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversationIds);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(conversationIds.Count, IConversationReadAccess.MaxBatch);
        return conversationIds.Count == 0
            ? new HashSet<Guid>()
            : await conversations.ReadableConversationsAsync(organizationId, accountId, conversationIds, cancellationToken);
    }
}
