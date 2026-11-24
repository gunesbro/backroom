using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Backroom.BuildingBlocks.Infrastructure.Messaging;
using Backroom.BuildingBlocks.Infrastructure.Persistence;

namespace Backroom.Modules.Messaging.Infrastructure.Migrations;

/// <summary>
/// Channels, messages and the module's outbox (docs/contracts/phase-1.md, "Data"), each under row-level security.
/// </summary>
[DbContext(typeof(MessagingDbContext))]
[Migration("20261124120000_CreateChannelsAndMessages")]
internal sealed class CreateChannelsAndMessages : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql("""
            CREATE TABLE messaging.channels (
                id uuid PRIMARY KEY,
                org_id uuid NOT NULL,
                name text NOT NULL CHECK (char_length(name) BETWEEN 1 AND 80),
                -- The name compared case-insensitively with the invariant culture, computed by the application.
                name_key text NOT NULL CHECK (char_length(name_key) BETWEEN 1 AND 160),
                created_by uuid NOT NULL,
                created_at timestamptz NOT NULL,
                archived_at timestamptz);

            -- One open channel per name in an organization.
            CREATE UNIQUE INDEX channels_name_key_idx ON messaging.channels (org_id, name_key) WHERE archived_at IS NULL;

            -- The list of open channels (keyset by id).
            CREATE INDEX channels_open_idx ON messaging.channels (org_id, id) WHERE archived_at IS NULL;

            CREATE TABLE messaging.messages (
                id uuid PRIMARY KEY,
                org_id uuid NOT NULL,
                channel_id uuid NOT NULL REFERENCES messaging.channels (id),
                author_account_id uuid NOT NULL,
                client_message_id uuid NOT NULL,
                -- Escaped text: at most 4000 characters, each at most six long after escaping (MessageText.MaxStoredLength).
                text text NOT NULL CHECK (char_length(text) BETWEEN 1 AND 24000),
                blocks jsonb NOT NULL CHECK (jsonb_typeof(blocks) = 'array'),
                created_at timestamptz NOT NULL,
                edited_at timestamptz,
                deleted_at timestamptz,
                -- Makes sending idempotent: a repeat finds the first message.
                CONSTRAINT messages_client_message_id_key UNIQUE (channel_id, author_account_id, client_message_id));

            -- History (before) and catch-up (after) read a channel's messages by id, which is time order (UUIDv7).
            CREATE INDEX messages_channel_id_id_idx ON messaging.messages (channel_id, id);
            """);
        migrationBuilder.EnableOrganizationRowLevelSecurity(MessagingDbContext.Schema, "channels");
        migrationBuilder.EnableOrganizationRowLevelSecurity(MessagingDbContext.Schema, "messages");
        migrationBuilder.CreateOutboxTable(MessagingDbContext.Schema);
    }
}
