using MessagePack;

namespace Backroom.Modules.Realtime.Contracts;

/// <summary>
/// A message as live clients receive it. <paramref name="Text"/> is the plain text (never escaped; clients escape when
/// they render). <paramref name="Seq"/> is its place in the channel in commit order: clients resume with <c>after={lastSeenSeq}</c>.
/// </summary>
[MessagePackObject]
public sealed record LiveMessage(
    [property: Key("id")] Guid Id,
    [property: Key("seq")] long Seq,
    [property: Key("clientMessageId")] Guid ClientMessageId,
    [property: Key("authorAccountId")] Guid AuthorAccountId,
    [property: Key("text")] string Text,
    [property: Key("createdAt")] DateTimeOffset CreatedAt);
