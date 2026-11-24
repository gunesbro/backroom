namespace Backroom.Modules.Messaging.Application.Conversations;

/// <summary>A conversation in a person's list. <paramref name="Name"/> is <see langword="null"/> for kinds that have none.</summary>
internal sealed record ConversationEntry(Guid Id, string Kind, string? Name, DateTimeOffset CreatedAt);
