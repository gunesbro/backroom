using System.Diagnostics.CodeAnalysis;
using Npgsql;
using NpgsqlTypes;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.Modules.Notifications.Application;
using Backroom.Modules.Notifications.Application.Bell;
using Backroom.Modules.Notifications.Application.Settings;

namespace Backroom.Modules.Notifications.Infrastructure;

/// <summary>
/// Settings and the bell. Row-level security limits every statement to the entered organization, and each statement also names
/// it; every read is bounded by its primary key, an array of at most 500 accounts, or a limit on an index.
/// </summary>
internal sealed class NotificationStore(DatabaseSession session) : INotificationStore
{
    private const string AccountSettingsSql = """
        SELECT account_id, default_level, org_muted_until, sound, hide_previews
        FROM notifications.account_settings
        WHERE account_id = ANY($1)
        """;

    private const string SetAccountSettingsSql = """
        INSERT INTO notifications.account_settings (org_id, account_id, default_level, org_muted_until, sound, hide_previews)
        VALUES ($1, $2, $3, $4, $5, $6)
        ON CONFLICT (org_id, account_id) DO UPDATE
        SET default_level = EXCLUDED.default_level, org_muted_until = EXCLUDED.org_muted_until,
            sound = EXCLUDED.sound, hide_previews = EXCLUDED.hide_previews
        """;

    private const string ConversationSettingsSql = """
        SELECT account_id, level, followed_thread_replies, ignore_group_mentions, sound
        FROM notifications.conversation_settings
        WHERE conversation_id = $1 AND account_id = ANY($2)
        """;

    private const string SetConversationSettingsSql = """
        INSERT INTO notifications.conversation_settings (org_id, account_id, conversation_id, level, followed_thread_replies, ignore_group_mentions, sound)
        VALUES ($1, $2, $3, $4, $5, $6, $7)
        ON CONFLICT (org_id, account_id, conversation_id) DO UPDATE
        SET level = EXCLUDED.level, followed_thread_replies = EXCLUDED.followed_thread_replies,
            ignore_group_mentions = EXCLUDED.ignore_group_mentions, sound = EXCLUDED.sound
        """;

    // One statement for the whole batch; a second entry for the same message and person (or a redelivery) adds nothing.
    private const string AddItemsSql = """
        INSERT INTO notifications.inbox_items (id, org_id, account_id, kind, conversation_id, message_id, actor_account_id, created_at)
        SELECT * FROM unnest($1::uuid[], $2::uuid[], $3::uuid[], $4::text[], $5::uuid[], $6::uuid[], $7::uuid[], $8::timestamptz[])
        ON CONFLICT DO NOTHING
        RETURNING id
        """;

    private const string ListColumns = "SELECT id, kind, conversation_id, message_id, actor_account_id, created_at, read_at FROM notifications.inbox_items WHERE org_id = $1 AND account_id = $2";
    private const string MentionsFilter = " AND kind IN ('mention', 'group_mention', 'quoted_reply')";
    private const string AfterFilter = " AND (created_at, id) < ($4, $5)";
    private const string ListOrder = " ORDER BY created_at DESC, id DESC LIMIT $3";

    private const string MarkReadSql = """
        UPDATE notifications.inbox_items SET read_at = COALESCE(read_at, $4)
        WHERE org_id = $1 AND account_id = $2 AND id = $3
        """;

    private const string MarkAllReadSql = """
        UPDATE notifications.inbox_items SET read_at = $3
        WHERE org_id = $1 AND account_id = $2 AND read_at IS NULL
        """;

    public async Task<IReadOnlyDictionary<Guid, AccountSettings>> GetAccountSettingsAsync(
        IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(accountIds);
        var result = new Dictionary<Guid, AccountSettings>();
        await using var command = new NpgsqlCommand(AccountSettingsSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid[]> { TypedValue = [.. accountIds] });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result[reader.GetGuid(0)] = new AccountSettings(
                Level(reader.GetString(1)),
                await reader.IsDBNullAsync(2, cancellationToken) ? null : reader.GetFieldValue<DateTimeOffset>(2),
                reader.GetBoolean(3),
                reader.GetBoolean(4));
        }

        return result;
    }

    public async Task SetAccountSettingsAsync(Guid organizationId, Guid accountId, AccountSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await using var command = new NpgsqlCommand(SetAccountSettingsSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organizationId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = accountId });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = NotificationLevelNames.Of(settings.DefaultLevel) });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset?> { NpgsqlDbType = NpgsqlDbType.TimestampTz, TypedValue = settings.OrgMutedUntil });
        command.Parameters.Add(new NpgsqlParameter<bool> { TypedValue = settings.Sound });
        command.Parameters.Add(new NpgsqlParameter<bool> { TypedValue = settings.HidePreviews });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<Guid, ConversationSettings>> GetConversationSettingsAsync(
        Guid conversationId, IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(accountIds);
        var result = new Dictionary<Guid, ConversationSettings>();
        await using var command = new NpgsqlCommand(ConversationSettingsSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = conversationId });
        command.Parameters.Add(new NpgsqlParameter<Guid[]> { TypedValue = [.. accountIds] });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result[reader.GetGuid(0)] = new ConversationSettings(Level(reader.GetString(1)), reader.GetBoolean(2), reader.GetBoolean(3), reader.GetBoolean(4));
        }

        return result;
    }

    public async Task SetConversationSettingsAsync(
        Guid organizationId, Guid accountId, Guid conversationId, ConversationSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await using var command = new NpgsqlCommand(SetConversationSettingsSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organizationId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = accountId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = conversationId });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = NotificationLevelNames.Of(settings.Level) });
        command.Parameters.Add(new NpgsqlParameter<bool> { TypedValue = settings.FollowedThreadReplies });
        command.Parameters.Add(new NpgsqlParameter<bool> { TypedValue = settings.IgnoreGroupMentions });
        command.Parameters.Add(new NpgsqlParameter<bool> { TypedValue = settings.Sound });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlySet<Guid>> AddItemsAsync(IReadOnlyList<NewInboxItem> items, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        var added = new HashSet<Guid>();
        if (items.Count == 0)
        {
            return added;
        }

        await using var command = new NpgsqlCommand(AddItemsSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid[]> { TypedValue = [.. items.Select(item => item.Id)] });
        command.Parameters.Add(new NpgsqlParameter<Guid[]> { TypedValue = [.. items.Select(item => item.OrgId)] });
        command.Parameters.Add(new NpgsqlParameter<Guid[]> { TypedValue = [.. items.Select(item => item.AccountId)] });
        command.Parameters.Add(new NpgsqlParameter<string[]> { TypedValue = [.. items.Select(item => item.Kind)] });
        command.Parameters.Add(new NpgsqlParameter<Guid?[]> { TypedValue = [.. items.Select(item => item.ConversationId)] });
        command.Parameters.Add(new NpgsqlParameter<Guid?[]> { TypedValue = [.. items.Select(item => item.MessageId)] });
        command.Parameters.Add(new NpgsqlParameter<Guid[]> { TypedValue = [.. items.Select(item => item.ActorAccountId)] });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset[]> { TypedValue = [.. items.Select(item => item.CreatedAt)] });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            added.Add(reader.GetGuid(0));
        }

        return added;
    }

    [SuppressMessage("Security", "CA2100", Justification = "The SQL is built only from this class's constants; values are parameters.")]
    public async Task<IReadOnlyList<InboxItem>> ListAsync(
        Guid organizationId, Guid accountId, bool mentionsOnly, (DateTimeOffset CreatedAt, Guid Id)? after, int count, CancellationToken cancellationToken)
    {
        var sql = ListColumns + (mentionsOnly ? MentionsFilter : string.Empty) + (after is null ? string.Empty : AfterFilter) + ListOrder;
        await using var command = new NpgsqlCommand(sql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organizationId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = accountId });
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = count });
        if (after is { } cursor)
        {
            command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = cursor.CreatedAt });
            command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = cursor.Id });
        }

        var items = new List<InboxItem>(Math.Min(count, 101));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new InboxItem(
                reader.GetGuid(0),
                reader.GetString(1),
                await reader.IsDBNullAsync(2, cancellationToken) ? null : reader.GetGuid(2),
                await reader.IsDBNullAsync(3, cancellationToken) ? null : reader.GetGuid(3),
                reader.GetGuid(4),
                reader.GetFieldValue<DateTimeOffset>(5),
                await reader.IsDBNullAsync(6, cancellationToken) ? null : reader.GetFieldValue<DateTimeOffset>(6)));
        }

        return items;
    }

    public async Task<bool> MarkReadAsync(Guid organizationId, Guid accountId, Guid itemId, DateTimeOffset readAt, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(MarkReadSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organizationId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = accountId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = itemId });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = readAt });
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<int> MarkAllReadAsync(Guid organizationId, Guid accountId, DateTimeOffset readAt, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(MarkAllReadSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organizationId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = accountId });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = readAt });
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static NotificationLevel Level(string name) =>
        NotificationLevelNames.TryParse(name, out var level) ? level : throw new InvalidOperationException("A stored notification level is unknown.");
}
