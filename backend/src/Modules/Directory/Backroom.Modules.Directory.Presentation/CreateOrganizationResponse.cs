namespace Backroom.Modules.Directory.Presentation;

/// <summary>The organization is being provisioned in <see cref="CellId"/>; poll <c>GET /api/v1/organizations/{id}</c>.</summary>
public sealed record CreateOrganizationResponse(Guid Id, string Status, string CellId, Uri CellUrl);
