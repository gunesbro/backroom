namespace Backroom.Modules.Directory.Application.Invitations;

/// <summary>
/// The control plane keeps invited emails only as an HMAC with its own key (docs/contracts/phase-1.md, "Privacy and
/// retention"), so it can compare an accepting account's verified email without storing the invited address.
/// </summary>
internal interface IInvitationEmailHmac
{
    /// <summary>
    /// The HMAC of the invitation id and the email normalized as the cell normalizes it (<c>EmailAddress</c>), or
    /// <see langword="null"/> if it is not one valid address. The id makes the value differ between invitations.
    /// </summary>
    byte[]? TryCompute(Guid invitationId, string? email);
}
