namespace Backroom.Modules.Messaging.Application.Channels;

internal sealed record ChannelEntry(Guid Id, string Name, DateTimeOffset CreatedAt);
