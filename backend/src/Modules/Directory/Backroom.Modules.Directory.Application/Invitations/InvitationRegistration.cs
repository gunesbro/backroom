namespace Backroom.Modules.Directory.Application.Invitations;

/// <summary>A checked <c>InvitationRegistered</c>: the hash of the code and the HMAC of the invited email, never either in clear.</summary>
internal sealed record InvitationRegistration(
    Guid InvitationId,
    Guid OrganizationId,
    string CellId,
    byte[] CodeHash,
    byte[] EmailHmac,
    string Role,
    DateTimeOffset ExpiresAt,
    int CodeVersion);
