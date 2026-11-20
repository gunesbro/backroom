namespace Backroom.Modules.Organization.Contracts;

/// <summary>
/// The control plane can resolve an invitation's code (docs/contracts/phase-1.md, flow 3, step 3). Called by
/// ControlPlaneConnector once the control plane acknowledged <c>InvitationRegistered</c> for that code version, in a
/// unit of work in the invitation's organization.
/// </summary>
public interface IInvitationRegistration
{
    /// <summary>
    /// Records that <paramref name="codeVersion"/> is registered and publishes <see cref="InvitationReady"/>, so the
    /// email can be sent. Does nothing, and returns <see langword="false"/>, if that version was already recorded, a
    /// resend replaced it, the invitation is no longer pending or has expired, or its inviter can no longer invite
    /// into its role (acceptance would refuse it); so a repeated acknowledgement never sends a second email, and no
    /// email carries a link that cannot work.
    /// </summary>
    Task<bool> MarkRegisteredAsync(Guid invitationId, int codeVersion, CancellationToken cancellationToken);
}
