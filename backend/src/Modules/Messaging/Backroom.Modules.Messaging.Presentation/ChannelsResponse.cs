namespace Backroom.Modules.Messaging.Presentation;

public sealed record ChannelsResponse(IReadOnlyList<ChannelResponse> Channels, Guid? Next);
