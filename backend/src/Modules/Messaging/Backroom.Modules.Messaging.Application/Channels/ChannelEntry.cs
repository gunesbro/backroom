namespace Backroom.Modules.Messaging.Application.Channels;

internal sealed record ChannelEntry(Guid Id, string Kind, string Name, string? Description, DateTimeOffset CreatedAt);
