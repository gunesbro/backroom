namespace Backroom.Modules.Messaging.Application.Messages;

/// <summary>
/// A stored message. <paramref name="Seq"/> is its place in the conversation, in commit order, and
/// <paramref name="VersionSeq"/> the number of its latest change (docs/adr/0011). <paramref name="Blocks"/> is the
/// stored JSON of its blocks (validated when it was written). A deleted message is a tombstone:
/// <paramref name="DeletedAt"/> is set and <paramref name="Text"/>, <paramref name="Blocks"/> and <paramref name="EditedAt"/> are <see langword="null"/>.
/// <paramref name="EditedAt"/> is the time of the latest edit, <see langword="null"/> for a message never edited.
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
    DateTimeOffset? DeletedAt,
    DateTimeOffset? EditedAt = null);
