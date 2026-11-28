using System.Text.Json.Serialization;

namespace Backroom.Modules.Messaging.Presentation;

/// <summary>
/// A message in a page. <c>VersionSeq</c> is the number of its latest change. <c>Blocks</c> is the rich text as a JSON
/// array in the block schema (docs/contracts/phase-2.md, "Block schema") and <c>Text</c> its plain form. A deleted
/// message is a tombstone: <c>DeletedAt</c> is set and <c>ClientMessageId</c>, <c>AuthorAccountId</c>, <c>Text</c> and
/// <c>Blocks</c> are null.
/// </summary>
public sealed record MessageResponse(
    Guid Id,
    long Seq,
    long VersionSeq,
    Guid? ClientMessageId,
    Guid? AuthorAccountId,
    string? Text,
    [property: JsonConverter(typeof(RawJsonConverter))] string? Blocks,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DeletedAt,
    DateTimeOffset? EditedAt = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MessageQuoteResponse? ReplyTo = null);
