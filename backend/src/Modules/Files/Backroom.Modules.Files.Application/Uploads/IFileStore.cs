using Backroom.Modules.Files.Contracts;

namespace Backroom.Modules.Files.Application.Uploads;

/// <summary>The <c>files.files</c> table, in the request's unit of work; row-level security limits it to the entered organization.</summary>
internal interface IFileStore
{
    Task AddAsync(NewFile file, CancellationToken cancellationToken);

    Task<FileRecord?> FindAsync(Guid organizationId, Guid fileId, CancellationToken cancellationToken);

    /// <summary>Moves a pending upload to ready with the confirmed type; false if it was no longer pending.</summary>
    Task<bool> MarkReadyAsync(Guid organizationId, Guid fileId, string detectedType, FileKind kind, DateTimeOffset readyAt, CancellationToken cancellationToken);
}
