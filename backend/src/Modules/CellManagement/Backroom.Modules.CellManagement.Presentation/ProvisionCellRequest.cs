namespace Backroom.Modules.CellManagement.Presentation;

public sealed record ProvisionCellRequest(string Id, string Name, string Type, string Region, string PublicUrl);
