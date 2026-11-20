namespace Backroom.Modules.Organization.Application.Invitations;

/// <summary>Invitations in the current unit of work, which row-level security limits to the entered organization.</summary>
internal interface IInvitationStore
{
    /// <summary>
    /// Makes invitation creation in the organization wait its turn until the unit of work ends, so the limits hold
    /// under concurrency. Code that changes invitations takes this lock before touching invitation rows, so the
    /// order of locks is the same everywhere.
    /// </summary>
    Task LockOrganizationAsync(Guid organizationId, CancellationToken cancellationToken);

    /// <summary>
    /// <c>active</c> or <c>deactivated</c> if a member in that state has the email (compared in ASCII lower case);
    /// otherwise <see langword="null"/>.
    /// </summary>
    Task<string?> FindMemberStatusAsync(string email, CancellationToken cancellationToken);

    /// <summary>Who created the pending invitation to the email, if there is one.</summary>
    Task<Guid?> FindPendingInviterAsync(string email, CancellationToken cancellationToken);

    Task<RecentInvitations> CountRecentAsync(Guid inviterAccountId, string email, DateTimeOffset since, CancellationToken cancellationToken);

    /// <summary>Pending invitations to other addresses than <paramref name="email"/>, whose own pending invitation is about to be replaced.</summary>
    Task<int> CountPendingExceptAsync(string email, CancellationToken cancellationToken);

    /// <summary>Revokes the pending invitation to the email, if any, and returns its id.</summary>
    Task<IReadOnlyList<Guid>> RevokePendingAsync(string email, Guid revokedBy, DateTimeOffset now, CancellationToken cancellationToken);

    Task AddAsync(NewInvitation invitation, CancellationToken cancellationToken);

    Task<InvitationState?> GetAsync(Guid invitationId, CancellationToken cancellationToken);

    /// <summary>Revokes the invitation if it is still pending; whether it was.</summary>
    Task<bool> RevokeAsync(Guid invitationId, Guid revokedBy, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Gives a pending invitation a new code as <paramref name="codeVersion"/>, so the old link stops working, and
    /// records the resend for the limits; whether it was still pending. The control plane must register it again.
    /// </summary>
    Task<bool> ReissueAsync(Guid invitationId, IssuedInvitationCode code, int codeVersion, Guid resentBy, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>The code hash of a pending, unexpired invitation if its code version is <paramref name="codeVersion"/>.</summary>
    Task<byte[]?> GetPendingCodeHashAsync(Guid invitationId, int codeVersion, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// What the link of a pending, unexpired invitation is built from, with its organization's name, if its code version
    /// is <paramref name="codeVersion"/> and that version is registered with the control plane.
    /// </summary>
    Task<InvitationLinkSource?> GetLinkSourceAsync(Guid invitationId, int codeVersion, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Records that the control plane registered <paramref name="codeVersion"/> of a pending, unexpired invitation, if
    /// that version is current and not recorded yet; whether it did.
    /// </summary>
    Task<bool> MarkRegisteredAsync(Guid invitationId, int codeVersion, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Marks the invitation accepted by <paramref name="acceptedBy"/> if it is still pending; whether it was.</summary>
    Task<bool> MarkAcceptedAsync(Guid invitationId, Guid acceptedBy, DateTimeOffset acceptedAt, CancellationToken cancellationToken);

    /// <summary>Pending invitations after <paramref name="after"/> by id, at most <paramref name="count"/>; only <paramref name="createdBy"/>'s if given.</summary>
    Task<IReadOnlyList<InvitationEntry>> ListPendingAsync(Guid? createdBy, Guid after, int count, CancellationToken cancellationToken);
}
