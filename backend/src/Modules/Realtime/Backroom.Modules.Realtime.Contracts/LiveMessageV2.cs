using MessagePack;

namespace Backroom.Modules.Realtime.Contracts;

/// <summary>
/// A message in <c>MessagePosted.v2</c>. <paramref name="Seq"/> is its place in the conversation in commit order and
/// <paramref name="VersionSeq"/> the number of its latest change (equal for a new message). <paramref name="Text"/> is
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
    [property: Key("text")] string Text,
    [property: Key("createdAt")] DateTimeOffset CreatedAt);
