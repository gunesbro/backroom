namespace Backroom.Modules.Organization.Application.Invitations;

/// <summary>A pending invitation as its inviter or a manager sees it; there is no code to show.</summary>
internal sealed record InvitationEntry(Guid Id, string Email, string Role, Guid CreatedBy, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);
