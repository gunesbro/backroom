namespace Backroom.Modules.Directory.Application.Invitations;

/// <summary>
/// An account's acceptance of an invitation: <c>accepting</c>, <c>used</c> or <c>failed</c> with the cell's
/// <paramref name="FailureCode"/>.
/// </summary>
internal sealed record InvitationAcceptanceState(Guid OrganizationId, string Status, string? FailureCode);
