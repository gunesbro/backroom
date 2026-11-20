namespace Backroom.Modules.Organization.Application.Invitations;

/// <summary>Builds an invitation's link from its stored code material (docs/contracts/phase-1.md, "Invitation security", Transport).</summary>
internal interface IInvitationLinkBuilder
{
    /// <summary>
    /// <c>https://&lt;web origin&gt;/invite#&lt;code&gt;</c>, or <see langword="null"/> if the key version is no longer
    /// configured or the derived code does not match the stored hash (the key behind that version changed), so a link
    /// that cannot work is never sent.
    /// </summary>
    Uri? Build(Guid invitationId, InvitationLinkSource source);
}
