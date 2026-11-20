namespace Backroom.Modules.Organization.Application.Invitations;

/// <summary>
/// What the link of a registered, pending invitation is built from: the stored salt, key version and code hash (the
/// code is derived again, never stored), the invited address and the organization's name. The caller clears
/// <see cref="Salt"/> after use.
/// </summary>
internal sealed record InvitationLinkSource(string Email, string OrganizationName, byte[] Salt, int KeyVersion, byte[] CodeHash)
{
    /// <summary>The type name only: the salt and the address must not reach a log.</summary>
    public override string ToString() => nameof(InvitationLinkSource);
}
