using Backroom.Modules.Notifications.Application.Bell;
using Backroom.Modules.Notifications.Application.Settings;

namespace Backroom.Modules.Notifications.Application;

/// <summary>
/// Settings and the bell in the current unit of work, which row-level security limits to the entered organization. Every
/// list is bounded by its caller.
/// </summary>
internal interface INotificationStore
{
    /// <summary>The stored account settings of the accounts; accounts without a row are left out.</summary>
    Task<IReadOnlyDictionary<Guid, AccountSettings>> GetAccountSettingsAsync(IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken);

    Task SetAccountSettingsAsync(Guid organizationId, Guid accountId, AccountSettings settings, CancellationToken cancellationToken);

    /// <summary>The stored settings of the accounts for the conversation; accounts without a row are left out.</summary>
    Task<IReadOnlyDictionary<Guid, ConversationSettings>> GetConversationSettingsAsync(
        Guid conversationId, IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken);

    Task SetConversationSettingsAsync(
        Guid organizationId, Guid accountId, Guid conversationId, ConversationSettings settings, CancellationToken cancellationToken);

    /// <summary>
    /// Adds the entries. A person has at most one entry per message: the ids of the entries actually added are returned,
    /// so a redelivered event or a second kind for the same message adds nothing (and announces nothing).
    /// </summary>
    Task<IReadOnlySet<Guid>> AddItemsAsync(IReadOnlyList<NewInboxItem> items, CancellationToken cancellationToken);

    /// <summary>
    /// The account's entries, newest first by <c>(created_at, id)</c>, after the cursor, at most <paramref name="count"/>.
    /// </summary>
    Task<IReadOnlyList<InboxItem>> ListAsync(
        Guid organizationId, Guid accountId, bool mentionsOnly, (DateTimeOffset CreatedAt, Guid Id)? after, int count, CancellationToken cancellationToken);

    /// <summary>Marks one of the account's entries read; <see langword="false"/> if the account has no such entry.</summary>
    Task<bool> MarkReadAsync(Guid organizationId, Guid accountId, Guid itemId, DateTimeOffset readAt, CancellationToken cancellationToken);

    /// <summary>Marks all of the account's unread entries read.</summary>
    Task<int> MarkAllReadAsync(Guid organizationId, Guid accountId, DateTimeOffset readAt, CancellationToken cancellationToken);
}
