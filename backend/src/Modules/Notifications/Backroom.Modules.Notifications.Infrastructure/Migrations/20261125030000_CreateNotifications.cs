using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Backroom.BuildingBlocks.Infrastructure.Messaging;
using Backroom.BuildingBlocks.Infrastructure.Persistence;

namespace Backroom.Modules.Notifications.Infrastructure.Migrations;

/// <summary>
/// Preferences and the bell (docs/contracts/phase-2.md, "Data"), each under row-level security, and the inbox of the
/// <c>messaging.activity-added.v1</c> consumer. Web Push subscriptions come with their step. The bell keeps ids only (decision 13).
/// </summary>
[DbContext(typeof(NotificationsDbContext))]
[Migration("20261125030000_CreateNotifications")]
internal sealed class CreateNotifications : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql("""
            CREATE TABLE notifications.account_settings (
                org_id uuid NOT NULL,
                account_id uuid NOT NULL,
                default_level text NOT NULL CHECK (default_level IN ('all', 'mentions', 'none')),
                -- Silent until then; the bell still collects.
                org_muted_until timestamptz,
                sound boolean NOT NULL,
                -- The live notification carries no text.
                hide_previews boolean NOT NULL,
                PRIMARY KEY (org_id, account_id));

            CREATE TABLE notifications.conversation_settings (
                org_id uuid NOT NULL,
                account_id uuid NOT NULL,
                conversation_id uuid NOT NULL,
                level text NOT NULL CHECK (level IN ('all', 'mentions', 'none')),
                followed_thread_replies boolean NOT NULL,
                ignore_group_mentions boolean NOT NULL,
                sound boolean NOT NULL,
                PRIMARY KEY (org_id, account_id, conversation_id));

            -- Ids only: the text shown with an entry is read through the permission check when it is shown.
            CREATE TABLE notifications.inbox_items (
                id uuid PRIMARY KEY,
                org_id uuid NOT NULL,
                account_id uuid NOT NULL,
                kind text NOT NULL CHECK (kind IN (
                    'mention', 'group_mention', 'quoted_reply', 'thread_reply', 'dm_message', 'added',
                    'reminder_due', 'scheduled_failed', 'invitation_accepted')),
                conversation_id uuid,
                message_id uuid,
                actor_account_id uuid NOT NULL,
                created_at timestamptz NOT NULL,
                read_at timestamptz);

            -- At most one notification per message per person.
            CREATE UNIQUE INDEX inbox_items_message_key ON notifications.inbox_items (org_id, account_id, message_id) WHERE message_id IS NOT NULL;
            -- The bell, newest first: keyset on (created_at, id).
            CREATE INDEX inbox_items_bell_idx ON notifications.inbox_items (org_id, account_id, created_at DESC, id DESC);
            -- The Mentions tab.
            CREATE INDEX inbox_items_mentions_idx ON notifications.inbox_items (org_id, account_id, created_at DESC, id DESC)
                WHERE kind IN ('mention', 'group_mention', 'quoted_reply');
            -- "Mark all read" touches only unread rows.
            CREATE INDEX inbox_items_unread_idx ON notifications.inbox_items (org_id, account_id) WHERE read_at IS NULL;
            """);
        migrationBuilder.EnableOrganizationRowLevelSecurity(NotificationsDbContext.Schema, "account_settings");
        migrationBuilder.EnableOrganizationRowLevelSecurity(NotificationsDbContext.Schema, "conversation_settings");
        migrationBuilder.EnableOrganizationRowLevelSecurity(NotificationsDbContext.Schema, "inbox_items");
        migrationBuilder.CreateInboxTable(NotificationsDbContext.Schema);
    }
}
