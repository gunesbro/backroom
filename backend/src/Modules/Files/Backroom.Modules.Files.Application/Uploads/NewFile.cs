using Backroom.Modules.Files.Contracts;

namespace Backroom.Modules.Files.Application.Uploads;

internal sealed record NewFile(
    Guid Id,
    Guid OrgId,
    Guid UploaderAccountId,
    Guid ConversationId,
    string Name,
    long Size,
    string DeclaredType,
    FileKind Kind,
    string StorageKey,
    DateTimeOffset CreatedAt);
