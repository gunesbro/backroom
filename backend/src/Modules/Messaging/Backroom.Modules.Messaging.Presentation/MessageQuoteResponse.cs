using System.Text.Json.Serialization;

namespace Backroom.Modules.Messaging.Presentation;

/// <summary>
/// The quote of a reply (docs/contracts/phase-2.md, flow 2). A deleted original answers <c>MessageId</c> and <c>Deleted</c> only:
/// no author, no text.
/// </summary>
public sealed record MessageQuoteResponse(
    Guid MessageId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? AuthorAccountId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Text,
    bool Deleted);
