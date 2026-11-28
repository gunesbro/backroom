using MessagePack;

namespace Backroom.Modules.Realtime.Contracts;

/// <summary>
/// A message as live clients receive it. <paramref name="Text"/> is the plain text (never escaped; clients escape when
/// they render). <paramref name="Blocks"/> is the rich text in the block schema (the same shape as in <see cref="LiveMessageV2"/>), an
/// additive field that older clients ignore; null when the sender sent plain text only (the message is then the single paragraph of <paramref name="Text"/>). <paramref name="Seq"/> is its place in the channel in commit order: clients resume with <c>after={lastSeenSeq}</c>.
/// </summary>
[MessagePackObject]
public sealed record LiveMessage(
    [property: Key("id")] Guid Id,
    [property: Key("seq")] long Seq,
    [property: Key("clientMessageId")] Guid ClientMessageId,
    [property: Key("authorAccountId")] Guid AuthorAccountId,
    [property: Key("text")] string Text,
    [property: Key("createdAt")] DateTimeOffset CreatedAt,
    [property: Key("blocks")] IReadOnlyList<object>? Blocks = null,
    [property: Key("replyTo")] LiveQuote? ReplyTo = null);
