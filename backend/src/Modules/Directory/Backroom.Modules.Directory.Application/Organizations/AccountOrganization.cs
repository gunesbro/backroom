namespace Backroom.Modules.Directory.Application.Organizations;

/// <summary>An organization of the signed-in account and the cell that holds its data.</summary>
internal sealed record AccountOrganization(Guid Id, string Name, string CellId, Uri CellUrl);
