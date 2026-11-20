namespace Backroom.Modules.Organization.Presentation;

public sealed record InvitationsResponse(IReadOnlyList<InvitationResponse> Invitations, Guid? Next);
