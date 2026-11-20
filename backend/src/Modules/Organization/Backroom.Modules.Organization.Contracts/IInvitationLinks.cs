namespace Backroom.Modules.Organization.Contracts;

/// <summary>
/// What other modules may learn about an invitation's code (docs/contracts/phase-1.md, flow 3). The code itself
/// never leaves this module except inside the link in the invitation email.
/// </summary>
public interface IInvitationLinks
{
    /// <summary>
    /// The SHA-256 of the invitation's code as 64 lower-case hexadecimal characters, for ControlPlaneConnector's
    /// <c>InvitationRegistered</c>; <see langword="null"/> if the invitation is no longer pending, has expired, or has
    /// another code version than <paramref name="codeVersion"/> (a resend replaced it, and its own event follows).
    /// Called in a unit of work in the invitation's organization.
    /// </summary>
    Task<string?> GetCodeHashAsync(Guid invitationId, int codeVersion, CancellationToken cancellationToken);

    /// <summary>
    /// The link for the Email module's invitation email, with the address and the organization's name;
    /// <see langword="null"/> unless the invitation is pending, unexpired, still at <paramref name="codeVersion"/> and
    /// that version is registered with the control plane (so the link already works). The code is derived again from
    /// the stored salt and key version, checked against the stored hash, and never stored. Called in a unit of work in
    /// the invitation's organization, in the worker role (which configures the web origin).
    /// </summary>
    Task<InvitationLink?> GetLinkAsync(Guid invitationId, int codeVersion, CancellationToken cancellationToken);
}
