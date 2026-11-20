namespace Backroom.Modules.Organization.Presentation;

/// <summary>A pending invitation. There is no code in it: the code exists only in the invitation email.</summary>
public sealed record InvitationResponse(Guid Id, string Email, string Role, Guid CreatedBy, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);
