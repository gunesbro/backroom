namespace Backroom.Modules.Messaging.Application.Messages;

/// <summary>A stored message; <paramref name="Text"/> is the escaped text.</summary>
internal sealed record MessageEntry(Guid Id, Guid ClientMessageId, Guid AuthorAccountId, string Text, DateTimeOffset CreatedAt);
