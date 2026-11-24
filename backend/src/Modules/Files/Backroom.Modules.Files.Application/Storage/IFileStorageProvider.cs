namespace Backroom.Modules.Files.Application.Storage;

/// <summary>
/// Chooses the storage of an organization (CLAUDE.md: every external service is selectable per organization). Phase 2
/// has one storage per cell, so every organization of the cell gets it; a per-organization choice plugs in here.
/// </summary>
internal interface IFileStorageProvider
{
    IFileStorage ForOrganization(Guid organizationId);
}
