namespace Backroom.Modules.Messaging.Application.Messages;

/// <summary>Messages in the current unit of work, which row-level security limits to the entered organization.</summary>
internal interface IMessageStore
{
    /// <summary>
    /// Adds the message and returns its blocks as stored; <see langword="null"/> if the author had already sent that client message id to the conversation
    /// (the unique index decides, so concurrent repeats cannot both be stored).
    /// </summary>
    Task<string?> AddAsync(NewMessage message, CancellationToken cancellationToken);

    /// <summary>Records that the message mentions the accounts (each once), all of whom can read the conversation.</summary>
    Task AddMentionsAsync(Guid organizationId, Guid conversationId, Guid messageId, IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken);

    /// <summary>Records the web addresses the message links to (each once), in their order in the message.</summary>
    Task AddLinksAsync(Guid organizationId, Guid conversationId, Guid messageId, IReadOnlyList<MessageLink> links, DateTimeOffset createdAt, CancellationToken cancellationToken);

    /// <summary>The message the author sent to the channel with that client message id, if any.</summary>
    Task<MessageEntry?> FindAsync(Guid channelId, Guid authorAccountId, Guid clientMessageId, CancellationToken cancellationToken);

    /// <summary>Messages (tombstones included) with a sequence number below <paramref name="before"/>, newest first, at most <paramref name="count"/>.</summary>
    Task<IReadOnlyList<MessageEntry>> ListBeforeAsync(Guid channelId, long before, int count, CancellationToken cancellationToken);

    /// <summary>Messages (tombstones included) with a sequence number above <paramref name="after"/>, oldest first, at most <paramref name="count"/>.</summary>
    Task<IReadOnlyList<MessageEntry>> ListAfterAsync(Guid channelId, long after, int count, CancellationToken cancellationToken);

    /// <summary>Messages whose latest change has a number above <paramref name="changedAfter"/> (tombstones included), in the order of those changes, at most <paramref name="count"/>.</summary>
    Task<IReadOnlyList<MessageEntry>> ListChangedAfterAsync(Guid conversationId, long changedAfter, int count, CancellationToken cancellationToken);

    /// <summary>The live (not deleted) messages among <paramref name="messageIds"/> with their conversation and text; unknown ones are left out.</summary>
    Task<IReadOnlyList<MessagePreviewSource>> ListForPreviewAsync(IReadOnlyCollection<Guid> messageIds, CancellationToken cancellationToken);

    /// <summary>The message's conversation, author and whether it is deleted, or <see langword="null"/> if the organization has no message with that id.</summary>
    Task<MessageHead?> FindHeadAsync(Guid messageId, CancellationToken cancellationToken);

    /// <summary>The message a reply would quote (conversation, author, deleted, text up to 400 characters), or <see langword="null"/> if the organization has none.</summary>
    Task<QuoteSource?> FindQuoteSourceAsync(Guid messageId, CancellationToken cancellationToken);

    /// <summary>
    /// Rewrites the content of a message that is not deleted: text, blocks, <c>version_seq</c> and <c>edited_at</c>. Returns the
    /// message as stored, or <see langword="null"/> if it is deleted. The caller holds the conversation row lock.
    /// </summary>
    Task<MessageEntry?> EditAsync(Guid messageId, long versionSeq, string text, string blocks, DateTimeOffset editedAt, CancellationToken cancellationToken);

    /// <summary>Removes the message's mention rows and returns the accounts they named.</summary>
    Task<IReadOnlyList<Guid>> DeleteMentionsAsync(Guid messageId, CancellationToken cancellationToken);

    /// <summary>Removes the message's link rows.</summary>
    Task DeleteLinksAsync(Guid messageId, CancellationToken cancellationToken);

    /// <summary>
    /// Turns the message into its tombstone in one statement: content erased (<c>text</c>, <c>blocks</c>, <c>edited_at</c>), its mention and link rows
    /// deleted, <c>deleted_at</c> and <c>version_seq</c> set. <see langword="false"/> if it was already deleted. The caller holds the conversation row lock.
    /// </summary>
    Task<bool> DeleteAsync(Guid messageId, long versionSeq, DateTimeOffset deletedAt, CancellationToken cancellationToken);
}
