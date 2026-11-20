namespace Backroom.Modules.Directory.Application.Invitations;

/// <summary>The control plane's invitations (docs/contracts/phase-1.md, flow 3, "States"), in the current unit of work.</summary>
internal interface IInvitationRegistry
{
    /// <summary>
    /// Adds the invitation, or replaces its code hash with a newer code version while it is pending. Never changes
    /// the status, and never recreates an invitation that a revocation reached first.
    /// </summary>
    Task<RegistryOutcome> RegisterAsync(InvitationRegistration registration, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Revokes a pending invitation; while it is being accepted, only records the request; for an unknown one, leaves
    /// a tombstone so that a late registration cannot bring it back.
    /// </summary>
    Task<RegistryOutcome> RevokeAsync(Guid organizationId, Guid invitationId, string cellId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>The invitation with this code hash and its organization, in any state; <see langword="null"/> if none.</summary>
    Task<InvitationByCode?> FindByCodeAsync(byte[] codeHash, CancellationToken cancellationToken);

    /// <summary>
    /// Takes the lock of the invitation with this code hash for the rest of the unit of work and reads it with its
    /// organization; <see langword="null"/> if none (or if a resend replaced the code meanwhile).
    /// </summary>
    Task<InvitationForAcceptance?> LockByCodeAsync(byte[] codeHash, CancellationToken cancellationToken);

    /// <summary>
    /// Moves a pending invitation (locked by <see cref="LockByCodeAsync"/>) to <c>accepting</c> for the account and
    /// gives the account a pending membership unless it has one that is pending or active. False if it was not pending.
    /// </summary>
    Task<bool> StartAcceptanceAsync(Guid invitationId, Guid organizationId, Guid accountId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// The cell added the member: an accepting invitation of an organization that maps to <paramref name="cellId"/>
    /// becomes <c>used</c> and the account's pending or removed membership active. Returns the account, or <see langword="null"/>
    /// if nothing was accepting there.
    /// </summary>
    Task<Guid?> CompleteAcceptanceAsync(Guid organizationId, Guid invitationId, string cellId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// The cell refused the acceptance: the invitation becomes <c>failed</c> with <paramref name="code"/>, and a pending
    /// membership an acceptance of the account made is removed again unless another of its acceptances there is still
    /// accepting. Returns the account, or <see langword="null"/> if
    /// nothing was accepting there.
    /// </summary>
    Task<Guid?> FailAcceptanceAsync(Guid organizationId, Guid invitationId, string cellId, string code, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>The account's most recent acceptance in any of its states; <see langword="null"/> if it never accepted one.</summary>
    Task<InvitationAcceptanceState?> FindLatestAcceptanceAsync(Guid accountId, CancellationToken cancellationToken);

    /// <summary>
    /// Marks up to <paramref name="limit"/> pending invitations whose expiry has come <c>expired</c>, oldest expiry
    /// first; skips the ones another transaction holds.
    /// </summary>
    Task<InvitationJobBatch> ExpireDueAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the email HMAC of up to <paramref name="limit"/> expired, revoked, failed or used invitations that
    /// ended (or were used) before <paramref name="endedBefore"/>; skips the ones another transaction holds.
    /// </summary>
    Task<InvitationJobBatch> ClearEndedEmailHmacsAsync(DateTimeOffset endedBefore, DateTimeOffset now, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes up to <paramref name="limit"/> revocation tombstones recorded before <paramref name="revokedBefore"/>;
    /// skips the ones another transaction holds.
    /// </summary>
    Task<InvitationJobBatch> DeleteTombstonesAsync(DateTimeOffset revokedBefore, int limit, CancellationToken cancellationToken);
}
