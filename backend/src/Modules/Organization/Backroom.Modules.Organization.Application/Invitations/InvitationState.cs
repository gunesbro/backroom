namespace Backroom.Modules.Organization.Application.Invitations;

/// <summary>An invitation as revoke and resend need it. The email is gone once retention has deleted it.</summary>
internal sealed record InvitationState(
    Guid Id,
    string? Email,
    string Role,
    string Status,
    Guid CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    int CodeVersion);
