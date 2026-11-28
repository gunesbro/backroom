using MessagePack;

namespace Backroom.Modules.Realtime.Contracts;

/// <summary>
/// A message in <c>MessagePosted.v2</c>. <paramref name="Seq"/> is its place in the conversation in commit order and
/// <paramref name="VersionSeq"/> the number of its latest change (equal for a new message). <paramref name="Blocks"/>
/// is the rich text in the block schema (docs/contracts/phase-2.md, "Block schema"), as lists and string-keyed maps of
/// plain values, the same shape as the JSON of the HTTP API; the publisher has validated it. <paramref name="Text"/> is
/// the plain text (never escaped; clients escape when they render).
/// </summary>
[MessagePackObject]
public sealed record LiveMessageV2(
    [property: Key("id")] Guid Id,
    [property: Key("seq")] long Seq,
    [property: Key("versionSeq")] long VersionSeq,
    [property: Key("kind")] string Kind,
    [property: Key("clientMessageId")] Guid ClientMessageId,
    [property: Key("authorAccountId")] Guid AuthorAccountId,
    [property: Key("blocks")] IReadOnlyList<object> Blocks,
    [property: Key("text")] string Text,
    [property: Key("createdAt")] DateTimeOffset CreatedAt,
    [property: Key("replyTo")] LiveQuote? ReplyTo = null);
