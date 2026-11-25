using Backroom.Modules.Files.Contracts;

namespace Backroom.Modules.Files.Application.Uploads;

/// <summary>The <c>files.files</c> table, in the request's unit of work; row-level security limits it to the entered organization.</summary>
internal interface IFileStore
{
    Task AddAsync(NewFile file, CancellationToken cancellationToken);

    Task<FileRecord?> FindAsync(Guid organizationId, Guid fileId, CancellationToken cancellationToken);

    /// <summary>Moves a pending upload to scanning with the confirmed type (the malware scan decides ready or rejected); false if it was no longer pending.</summary>
    Task<bool> MarkScanningAsync(Guid organizationId, Guid fileId, string detectedType, FileKind kind, CancellationToken cancellationToken);
}
