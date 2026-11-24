using Backroom.Modules.Files.Contracts;

namespace Backroom.Modules.Files.Application.Uploads;

/// <summary>A stored file. <see cref="Name"/> is for the API only: it is never logged or put in a key, URL or event.</summary>
internal sealed record FileRecord(
    Guid Id,
    Guid UploaderAccountId,
    Guid ConversationId,
    string Name,
    long Size,
    string DeclaredType,
    string? DetectedType,
    FileKind Kind,
    string StorageKey,
    FileStatus Status,
    DateTimeOffset CreatedAt);
