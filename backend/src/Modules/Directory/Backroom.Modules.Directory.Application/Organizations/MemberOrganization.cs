namespace Backroom.Modules.Directory.Application.Organizations;

/// <summary>An active organization the account is an active member of.</summary>
internal sealed record MemberOrganization(Guid Id, string Name, string CellId);
