namespace Backroom.Modules.Directory.Presentation;

/// <summary><c>accepting</c> until the cell answers (or <c>used</c> on a repeat); poll <c>GET /api/v1/invitations/acceptance</c>.</summary>
public sealed record AcceptedInvitationResponse(Guid OrganizationId, string Status);
