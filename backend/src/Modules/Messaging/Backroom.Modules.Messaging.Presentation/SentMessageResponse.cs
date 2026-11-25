namespace Backroom.Modules.Messaging.Presentation;

/// <summary>The answer to a send: the message with the cell <c>Epoch</c> its numbers belong to.</summary>
public sealed record SentMessageResponse(
    Guid Id,
    long Seq,
    long VersionSeq,
    Guid? ClientMessageId,
    Guid? AuthorAccountId,
    string? Text,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DeletedAt,
    string Epoch);
