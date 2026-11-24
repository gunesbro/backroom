namespace Backroom.Modules.Messaging.Application.Messages;

/// <summary><paramref name="Text"/> is already escaped and <paramref name="Blocks"/> derived from it (JSON).</summary>
internal sealed record NewMessage(
    Guid Id,
    Guid OrgId,
    Guid ChannelId,
    Guid AuthorAccountId,
    Guid ClientMessageId,
    string Text,
    string Blocks,
    DateTimeOffset CreatedAt);
