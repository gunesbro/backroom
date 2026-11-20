namespace Backroom.Modules.Directory.Presentation;

/// <summary>The account's latest acceptance: <c>accepting</c>, <c>used</c> or <c>failed</c> with the cell's <see cref="Code"/>.</summary>
public sealed record InvitationAcceptanceResponse(Guid OrganizationId, string Status, string? Code);
