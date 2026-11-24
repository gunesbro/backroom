namespace Backroom.Modules.Messaging.Application.Messages;

/// <summary>Messages in the current unit of work, which row-level security limits to the entered organization.</summary>
internal interface IMessageStore
{
    /// <summary>
    /// Adds the message; <see langword="false"/> if the author had already sent that client message id to the channel
    /// (the unique index decides, so concurrent repeats cannot both be stored).
    /// </summary>
    Task<bool> AddAsync(NewMessage message, CancellationToken cancellationToken);

    /// <summary>The message the author sent to the channel with that client message id, if any.</summary>
    Task<MessageEntry?> FindAsync(Guid channelId, Guid authorAccountId, Guid clientMessageId, CancellationToken cancellationToken);

    /// <summary>Messages with a sequence number below <paramref name="before"/>, newest first, at most <paramref name="count"/>.</summary>
    Task<IReadOnlyList<MessageEntry>> ListBeforeAsync(Guid channelId, long before, int count, CancellationToken cancellationToken);

    /// <summary>Messages with a sequence number above <paramref name="after"/>, oldest first, at most <paramref name="count"/>.</summary>
    Task<IReadOnlyList<MessageEntry>> ListAfterAsync(Guid channelId, long after, int count, CancellationToken cancellationToken);

    /// <summary>The live (not deleted) messages among <paramref name="messageIds"/> with their conversation and text; unknown ones are left out.</summary>
    Task<IReadOnlyList<MessagePreviewSource>> ListForPreviewAsync(IReadOnlyCollection<Guid> messageIds, CancellationToken cancellationToken);
}
