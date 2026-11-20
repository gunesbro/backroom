namespace Backroom.Modules.Directory.Application.Invitations;

/// <summary>An invitation found by its code hash, with its organization's name. Only pending ones carry an email HMAC and expiry for sure.</summary>
internal sealed record InvitationByCode(
    Guid InvitationId,
    Guid OrganizationId,
    string OrganizationName,
    string OrganizationStatus,
    string Status,
    DateTimeOffset? ExpiresAt,
    byte[]? EmailHmac);
