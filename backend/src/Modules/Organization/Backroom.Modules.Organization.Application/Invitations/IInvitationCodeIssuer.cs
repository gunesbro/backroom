namespace Backroom.Modules.Organization.Application.Invitations;

/// <summary>Issues invitation codes under the current key version (docs/contracts/phase-1.md, "Invitation security").</summary>
internal interface IInvitationCodeIssuer
{
    /// <summary>A code for the invitation with a new random salt. The code itself does not leave the issuer.</summary>
    IssuedInvitationCode Issue(Guid invitationId);
}
