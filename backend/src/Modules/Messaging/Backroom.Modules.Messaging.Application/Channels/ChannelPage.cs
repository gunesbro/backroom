namespace Backroom.Modules.Messaging.Application.Channels;

/// <summary>One page of channels in creation order; <paramref name="Next"/> is the <c>after</c> of the following page, if there is one.</summary>
internal sealed record ChannelPage(IReadOnlyList<ChannelEntry> Channels, Guid? Next);
