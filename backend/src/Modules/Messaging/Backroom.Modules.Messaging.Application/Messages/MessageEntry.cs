namespace Backroom.Modules.Messaging.Application.Messages;

/// <summary>
/// A stored message. <paramref name="Seq"/> is its place in the conversation, in commit order, and
/// <paramref name="VersionSeq"/> the number of its latest change (docs/adr/0011). <paramref name="Blocks"/> is the
/// stored JSON of its blocks (validated when it was written). A deleted message is a tombstone:
/// <paramref name="DeletedAt"/> is set and <paramref name="Text"/> and <paramref name="Blocks"/> are <see langword="null"/>.
/// </summary>
internal sealed record MessageEntry(
    Guid Id,
    long Seq,
    long VersionSeq,
    Guid ClientMessageId,
    Guid AuthorAccountId,
    string? Text,
    string? Blocks,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DeletedAt);
