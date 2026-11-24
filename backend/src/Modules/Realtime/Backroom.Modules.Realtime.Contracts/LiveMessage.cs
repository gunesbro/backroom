namespace Backroom.Modules.Realtime.Contracts;

/// <summary>A message as live clients receive it; <paramref name="Text"/> is the stored, escaped text.</summary>
public sealed record LiveMessage(Guid Id, Guid ClientMessageId, Guid AuthorAccountId, string Text, DateTimeOffset CreatedAt);
