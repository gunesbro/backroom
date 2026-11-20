namespace Backroom.Modules.Directory.Application.Organizations;

/// <summary>A new organization being provisioned in its cell.</summary>
internal sealed record CreatedOrganization(Guid Id, string Status, string CellId, Uri CellUrl);
