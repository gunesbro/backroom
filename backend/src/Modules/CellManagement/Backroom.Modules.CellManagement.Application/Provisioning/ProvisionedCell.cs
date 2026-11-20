namespace Backroom.Modules.CellManagement.Application.Provisioning;

internal sealed record ProvisionedCell(string Id, string RegistrationKey, DateTimeOffset ExpiresAt);
