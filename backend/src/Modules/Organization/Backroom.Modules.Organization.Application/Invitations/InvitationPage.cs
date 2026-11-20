namespace Backroom.Modules.Organization.Application.Invitations;

internal sealed record InvitationPage(IReadOnlyList<InvitationEntry> Invitations, Guid? Next);
