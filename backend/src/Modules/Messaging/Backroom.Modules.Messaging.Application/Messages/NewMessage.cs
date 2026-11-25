namespace Backroom.Modules.Messaging.Application.Messages;

/// <summary><paramref name="Blocks"/> is the canonical JSON and <paramref name="Text"/> its plain form (one is derived from the other); <paramref name="Seq"/> comes from the conversation counter.</summary>
internal sealed record NewMessage(
    Guid Id,
    Guid OrgId,
    Guid ChannelId,
    long Seq,
    Guid AuthorAccountId,
    Guid ClientMessageId,
    string Text,
    string Blocks,
    DateTimeOffset CreatedAt);
