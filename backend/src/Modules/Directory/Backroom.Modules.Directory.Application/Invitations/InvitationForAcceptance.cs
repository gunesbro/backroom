namespace Backroom.Modules.Directory.Application.Invitations;

/// <summary>
/// An invitation found by its code under its lock, with its organization's state and cell. Only pending and
/// accepting ones carry an email HMAC and expiry for sure; accepting, used and failed ones carry the accepting account.
/// </summary>
internal sealed record InvitationForAcceptance(
    Guid InvitationId,
    Guid OrganizationId,
    string OrganizationStatus,
    string CellId,
    string Status,
    DateTimeOffset? ExpiresAt,
    byte[]? EmailHmac,
    Guid? AcceptingAccountId);
