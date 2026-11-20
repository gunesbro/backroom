namespace Backroom.Modules.CellManagement.Presentation;

public sealed record ProvisionCellResponse(string Id, string RegistrationKey, DateTimeOffset ExpiresAt);
