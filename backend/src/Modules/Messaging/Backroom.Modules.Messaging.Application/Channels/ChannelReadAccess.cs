using Backroom.Modules.Messaging.Contracts;

namespace Backroom.Modules.Messaging.Application.Channels;

/// <summary>Phase 1: every open channel is public, so the account's membership (checked by the caller) is enough.</summary>
internal sealed class ChannelReadAccess(IChannelStore channels) : IChannelReadAccess
{
    public Task<bool> CanReadAsync(Guid organizationId, Guid channelId, Guid accountId, CancellationToken cancellationToken) =>
        channels.ExistsAsync(organizationId, channelId, cancellationToken);
}
