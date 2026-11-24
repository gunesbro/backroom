namespace Backroom.Modules.Directory.Contracts;

/// <summary>An organization assigned to a cell, as <c>FullSync</c> reports it.</summary>
public sealed record CellOrganization(Guid OrganizationId, string Name);
