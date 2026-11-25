namespace Backroom.Modules.Messaging.Presentation;

public sealed record ChannelResponse(Guid Id, string Kind, string Name, string? Description, DateTimeOffset CreatedAt);
