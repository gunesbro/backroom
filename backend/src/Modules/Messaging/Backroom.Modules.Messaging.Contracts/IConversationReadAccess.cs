namespace Backroom.Modules.Messaging.Contracts;

/// <summary>
/// Whether an account may read a conversation, for modules that hand out access to it (Realtime's group
/// subscriptions; later notifications, files and search). Phase 2 step 1 (docs/contracts/phase-2.md, flow 1): an open
/// (not archived) public channel of the organization, or any other open conversation the account is an open member
/// of. Threads (read access of the parent) and archived conversations (read-only, still readable) arrive with their
/// steps; until then they are refused. The caller has checked the organization membership. Every refusal looks the
/// same, so callers answer "not found" whether the conversation is unknown, of another organization or unreadable.
/// </summary>
public interface IConversationReadAccess
{
    /// <summary>Runs in the current unit of work, which row-level security limits to the entered organization.</summary>
    Task<bool> CanReadAsync(Guid organizationId, Guid conversationId, Guid accountId, CancellationToken cancellationToken);

    /// <summary>
    /// Whether the account holds an open membership of the conversation (of any kind, archived or not), for callers that
    /// must tell a member from a reader of a public channel (Realtime's late removal). Same unit of work as above.
    /// </summary>
    Task<bool> IsMemberAsync(Guid organizationId, Guid conversationId, Guid accountId, CancellationToken cancellationToken);

    /// <summary>The most accounts or conversations one batch call takes.</summary>
    public const int MaxBatch = 500;

    /// <summary>
    /// The accounts among <paramref name="accountIds"/> (at most <see cref="MaxBatch"/>) that are active members of the entered
    /// organization and may read the conversation, in two statements however many accounts there are. Organization members only, so a
    /// removed member or anyone outside the organization (a guest) is never in the answer. Same unit of work as above.
    /// </summary>
    Task<IReadOnlyList<Guid>> FilterReadersAsync(
        Guid organizationId, Guid conversationId, IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken);

    /// <summary>
    /// The conversations among <paramref name="conversationIds"/> (at most <see cref="MaxBatch"/>) the account may read now, in one
    /// statement. The caller has checked the organization membership. Same unit of work as above.
    /// </summary>
    Task<IReadOnlySet<Guid>> ReadableConversationsAsync(
        Guid organizationId, Guid accountId, IReadOnlyCollection<Guid> conversationIds, CancellationToken cancellationToken);
}
