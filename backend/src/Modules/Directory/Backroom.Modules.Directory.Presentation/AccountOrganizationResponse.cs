namespace Backroom.Modules.Directory.Presentation;

public sealed record AccountOrganizationResponse(Guid Id, string Name, string CellId, Uri CellUrl);
