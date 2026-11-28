using MessagePack;

namespace Backroom.Modules.Realtime.Contracts;

/// <summary>
/// What a reply shows of the message it quotes in <c>MessagePosted</c> (docs/contracts/phase-2.md, flow 2). A live original:
/// <paramref name="AuthorAccountId"/> and <paramref name="Text"/> (the derived text cut to 200 characters). A deleted original:
/// <paramref name="Deleted"/> and the id only. The quote is a snapshot of the moment the reply was posted; clients update it
/// from <c>MessageEdited</c> and <c>MessageDeleted</c> of the original, or read it again from the HTTP API.
/// </summary>
[MessagePackObject]
public sealed record LiveQuote(
    [property: Key("messageId")] Guid MessageId,
    [property: Key("authorAccountId")] Guid? AuthorAccountId,
    [property: Key("text")] string? Text,
    [property: Key("deleted")] bool Deleted);
