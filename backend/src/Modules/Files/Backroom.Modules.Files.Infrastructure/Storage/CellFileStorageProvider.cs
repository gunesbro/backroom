using Backroom.Modules.Files.Application.Storage;

namespace Backroom.Modules.Files.Infrastructure.Storage;

/// <summary>Phase 2: every organization of the cell uses the cell's storage.</summary>
internal sealed class CellFileStorageProvider(S3FileStorage storage) : IFileStorageProvider
{
    public IFileStorage ForOrganization(Guid organizationId) => storage;
}
