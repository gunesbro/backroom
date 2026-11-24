namespace Backroom.Modules.Messaging.Presentation;

public sealed record ChannelResponse(Guid Id, string Name, DateTimeOffset CreatedAt);
