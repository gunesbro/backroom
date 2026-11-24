namespace Backroom.Modules.Messaging.Application.Channels;

/// <summary>Channels in the current unit of work, which row-level security limits to the entered organization.</summary>
internal interface IChannelStore
{
    /// <summary>Adds the channel; <see langword="false"/> if an open channel of the organization already has that name.</summary>
    Task<bool> AddAsync(NewChannel channel, CancellationToken cancellationToken);

    /// <summary>Open (not archived) channels of the organization after <paramref name="after"/> by id, at most <paramref name="count"/>.</summary>
    Task<IReadOnlyList<ChannelEntry>> ListAsync(Guid organizationId, Guid after, int count, CancellationToken cancellationToken);

    /// <summary>Whether an open channel with the id exists in the organization.</summary>
    Task<bool> ExistsAsync(Guid organizationId, Guid channelId, CancellationToken cancellationToken);

    /// <summary>
    /// The channel next message sequence number, or <see langword="null"/> if there is no open channel with that id.
    /// The update holds the channel row lock until the unit of work ends, so sequence order is commit order: sends to
    /// one channel are serialized from here to their commit.
    /// </summary>
    Task<long?> NextSequenceAsync(Guid organizationId, Guid channelId, CancellationToken cancellationToken);
}
