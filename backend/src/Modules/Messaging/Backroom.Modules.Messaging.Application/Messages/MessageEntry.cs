namespace Backroom.Modules.Messaging.Application.Messages;

/// <summary>
/// A stored message. <paramref name="Seq"/> is its place in the conversation, in commit order, and
/// <paramref name="VersionSeq"/> the number of its latest change (docs/adr/0011). A deleted message is a tombstone:
/// <paramref name="DeletedAt"/> is set and <paramref name="Text"/> is <see langword="null"/>.
/// </summary>
internal sealed record MessageEntry(
    Guid Id,
    long Seq,
    long VersionSeq,
    Guid ClientMessageId,
    Guid AuthorAccountId,
    string? Text,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DeletedAt);
