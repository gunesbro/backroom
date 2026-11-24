namespace Backroom.Modules.Messaging.Application.Messages;

/// <summary><paramref name="Blocks"/> are derived from <paramref name="Text"/> (JSON); <paramref name="Seq"/> comes from the channel counter.</summary>
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
