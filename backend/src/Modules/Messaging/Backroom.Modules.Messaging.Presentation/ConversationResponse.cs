namespace Backroom.Modules.Messaging.Presentation;

/// <summary>A conversation in the caller's list; <paramref name="Name"/> is <see langword="null"/> for kinds without one.</summary>
public sealed record ConversationResponse(Guid Id, string Kind, string? Name, DateTimeOffset CreatedAt);
