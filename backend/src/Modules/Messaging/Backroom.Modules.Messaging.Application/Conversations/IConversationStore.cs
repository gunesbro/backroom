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
    /// The account's conversations (open membership, no threads): the ones that are not archived, or with
    /// <paramref name="archived"/> only the archived ones; after the row <paramref name="afterSortAt"/>, <paramref name="afterId"/> in the order newest message first (creation time when there is none), then id descending,
    /// at most <paramref name="count"/>.
    /// </summary>
    Task<IReadOnlyList<ConversationEntry>> ListForAccountAsync(Guid organizationId, Guid accountId, bool archived, DateTimeOffset afterSortAt, Guid afterId, int count, CancellationToken cancellationToken);

    /// <summary>The accounts among <paramref name="accountIds"/> that may read the conversation (<see cref="ConversationAccess.CanRead"/>), in one statement.</summary>
    Task<IReadOnlyList<Guid>> FilterReadersAsync(Guid organizationId, Guid conversationId, IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken);

    /// <summary>The conversations among <paramref name="conversationIds"/> the account may read, in one statement.</summary>
    Task<IReadOnlySet<Guid>> ReadableConversationsAsync(Guid organizationId, Guid accountId, IReadOnlyCollection<Guid> conversationIds, CancellationToken cancellationToken);

    /// <summary>
    /// The conversation with the account's role in it, or <see langword="null"/> if the organization has none with
    /// that id. With <paramref name="forUpdate"/> the conversation row is locked until the unit of work ends: every
    /// membership and management change starts with it, so those changes run one at a time per conversation. The
    /// role is then read after the lock is held, so it is the committed one, never the one from before a lock wait.
    /// </summary>
    Task<ConversationDetails?> GetAsync(Guid organizationId, Guid conversationId, Guid accountId, bool forUpdate, CancellationToken cancellationToken);

    /// <summary>
    /// Opens a membership as <c>member</c> for each account that has none open (new rows, or earlier ones reopened) and
    /// counts them on the conversation, in one statement; returns the accounts whose membership was opened.
    /// </summary>
    Task<IReadOnlyList<Guid>> OpenMembershipsAsync(
        Guid organizationId, Guid conversationId, IReadOnlyCollection<Guid> accountIds, DateTimeOffset joinedAt, long joinedSeq, Guid addedBy, CancellationToken cancellationToken);

    /// <summary>Whether any of the accounts already holds <paramref name="cap"/> open memberships in the organization, in one statement.</summary>
    Task<bool> AnyAtMembershipLimitAsync(Guid organizationId, IReadOnlyCollection<Guid> accountIds, int cap, CancellationToken cancellationToken);

    /// <summary>The conversation's open members after <paramref name="after"/> by account id, at most <paramref name="count"/>.</summary>
    Task<IReadOnlyList<ConversationMember>> ListMembersAsync(Guid organizationId, Guid conversationId, Guid after, int count, CancellationToken cancellationToken);

    /// <summary>The open member who joined earliest (ties: the lowest account id), if the conversation has a member.</summary>
    Task<Guid?> EarliestMemberAsync(Guid organizationId, Guid conversationId, CancellationToken cancellationToken);

    /// <summary>
    /// Makes <paramref name="ownerAccountId"/> the private channel's owner (its member row and the conversation's copy)
    /// and <paramref name="previousOwnerAccountId"/> a plain member. The caller holds the conversation row lock.
    /// <see langword="false"/>, with nothing written, if the new owner holds no open membership: an owner is always a
    /// current member.
    /// </summary>
    Task<bool> SetOwnerAsync(Guid organizationId, Guid conversationId, Guid? previousOwnerAccountId, Guid ownerAccountId, CancellationToken cancellationToken);

    /// <summary>
    /// Renames the channel (when <paramref name="name"/> is given) and sets its description (when
    /// <paramref name="setDescription"/>), taking the next change number. <see langword="null"/> if
    /// <paramref name="uniqueName"/> is set (a public channel) and another open public channel has that name.
    /// </summary>
    Task<ConversationChange?> UpdateAsync(
        Guid organizationId, Guid conversationId, string? name, string? nameKey, bool setDescription, string? description, bool uniqueName, CancellationToken cancellationToken);

    /// <summary>
    /// Archives the conversation at <paramref name="archivedAt"/>, or reopens it with <see langword="null"/>, taking the
    /// next change number. <see langword="null"/> if reopening a public channel whose name another open public channel
    /// has taken meanwhile.
    /// </summary>
    Task<ConversationChange?> SetArchivedAsync(Guid organizationId, Guid conversationId, DateTimeOffset? archivedAt, CancellationToken cancellationToken);

    /// <summary>
    /// Takes the conversation's next change number after a membership or ownership change and returns the conversation
    /// as it is now. The caller holds the conversation row lock.
    /// </summary>
    Task<ConversationChange> TouchAsync(Guid organizationId, Guid conversationId, CancellationToken cancellationToken);

    /// <summary>How many open private channels the organization has: a number and nothing else.</summary>
    Task<int> CountOpenPrivateChannelsAsync(Guid organizationId, CancellationToken cancellationToken);

    /// <summary>The channels the account created since <paramref name="since"/>, counted up to <paramref name="cap"/>.</summary>
    Task<RecentCount> RecentChannelCreationsAsync(Guid organizationId, Guid accountId, DateTimeOffset since, int cap, CancellationToken cancellationToken);

    /// <summary>The memberships of other people the account opened since <paramref name="since"/>, counted up to <paramref name="cap"/>.</summary>
    Task<RecentCount> RecentMemberAdditionsAsync(Guid organizationId, Guid accountId, DateTimeOffset since, int cap, CancellationToken cancellationToken);
}
