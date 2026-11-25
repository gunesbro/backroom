namespace Backroom.Modules.Messaging.Application.Channels;

/// <summary>Channels in the current unit of work, which row-level security limits to the entered organization.</summary>
internal interface IChannelStore
{
    /// <summary>
    /// Adds the public channel with no members yet; <see langword="false"/> if an open public channel of the
    /// organization already has that name. Private channels are never compared.
    /// </summary>
    Task<bool> AddAsync(NewChannel channel, CancellationToken cancellationToken);

    /// <summary>Open (not archived) public channels of the organization after <paramref name="after"/> by id, at most <paramref name="count"/>.</summary>
    Task<IReadOnlyList<ChannelEntry>> ListAsync(Guid organizationId, Guid after, int count, CancellationToken cancellationToken);

    /// <summary>
    /// The conversation's next message sequence number, or <see langword="null"/> if there is no open conversation with
    /// that id; the caller has checked read access. The update holds the conversation row lock until the unit of work
    /// ends, so sequence order is commit order: sends to one conversation are serialized from here to their commit.
    /// It also records <paramref name="sentAt"/> as the conversation's last message time.
    /// </summary>
    Task<long?> NextSequenceAsync(Guid organizationId, Guid channelId, DateTimeOffset sentAt, CancellationToken cancellationToken);

    /// <summary>
    /// Puts the conversation's newest message (<c>last_message_seq</c>, <c>last_message_at</c>) back to what the stored
    /// messages say, after <see cref="NextSequenceAsync"/> announced a message that was then not stored (a lost race of
    /// an idempotent repeat). The caller still holds the row lock; the change number taken stays unused (a gap).
    /// </summary>
    Task RestoreLastMessageAsync(Guid organizationId, Guid channelId, CancellationToken cancellationToken);
}
