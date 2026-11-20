namespace Backroom.Modules.Organization.Application.Invitations;

internal sealed record NewInvitation(
    Guid Id,
    Guid OrganizationId,
    string Email,
    string Role,
    IssuedInvitationCode Code,
    Guid CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);
