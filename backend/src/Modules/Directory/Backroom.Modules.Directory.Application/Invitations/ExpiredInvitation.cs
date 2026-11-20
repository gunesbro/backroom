namespace Backroom.Modules.Directory.Application.Invitations;

/// <summary>An invitation the expiry job marked <c>expired</c>, for its security log event (ids only).</summary>
internal sealed record ExpiredInvitation(Guid InvitationId, Guid OrganizationId);
