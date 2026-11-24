namespace Backroom.Modules.Messaging.Presentation;

/// <summary>
/// A message in a page. <c>VersionSeq</c> is the number of its latest change. A deleted message is a tombstone:
/// <c>DeletedAt</c> is set and <c>ClientMessageId</c>, <c>AuthorAccountId</c> and <c>Text</c> are null.
/// </summary>
public sealed record MessageResponse(
    Guid Id,
    long Seq,
    long VersionSeq,
    Guid? ClientMessageId,
    Guid? AuthorAccountId,
    string? Text,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DeletedAt);
