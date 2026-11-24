namespace Backroom.Modules.Messaging.Application.Conversations;

/// <summary>Conversations and their memberships in the current unit of work, which row-level security limits to the entered organization.</summary>
internal interface IConversationStore
{
    /// <summary>The conversation as <paramref name="accountId"/> sees it, or <see langword="null"/> if the organization has no conversation with that id.</summary>
    Task<ConversationAccess?> FindAsync(Guid organizationId, Guid conversationId, Guid accountId, CancellationToken cancellationToken);

    /// <summary>The account's open memberships in the organization, counted up to <paramref name="cap"/>.</summary>
    Task<int> CountOpenMembershipsAsync(Guid organizationId, Guid accountId, int cap, CancellationToken cancellationToken);

    /// <summary>
    /// Opens the membership (a new row, or the account's earlier one reopened) and counts it on the conversation;
    /// <see langword="false"/> if it was already open, in which case nothing changes.
    /// </summary>
    Task<bool> OpenMembershipAsync(NewMembership membership, CancellationToken cancellationToken);

    /// <summary>Closes the account's open membership and uncounts it; <see langword="false"/> if there was none.</summary>
    Task<bool> CloseMembershipAsync(Guid organizationId, Guid conversationId, Guid accountId, DateTimeOffset leftAt, CancellationToken cancellationToken);

    /// <summary>
    /// The account's open conversations (open membership, not archived, no threads) after the row <paramref name="afterSortAt"/>, <paramref name="afterId"/> in the order newest message first (creation time when there is none), then id descending,
    /// at most <paramref name="count"/>.
    /// </summary>
    Task<IReadOnlyList<ConversationEntry>> ListForAccountAsync(Guid organizationId, Guid accountId, DateTimeOffset afterSortAt, Guid afterId, int count, CancellationToken cancellationToken);
}
