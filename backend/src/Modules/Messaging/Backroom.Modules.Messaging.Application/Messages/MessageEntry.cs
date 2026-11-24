namespace Backroom.Modules.Messaging.Application.Messages;

/// <summary>A stored message. <paramref name="Seq"/> is its place in the channel, in commit order.</summary>
internal sealed record MessageEntry(Guid Id, long Seq, Guid ClientMessageId, Guid AuthorAccountId, string Text, DateTimeOffset CreatedAt);
