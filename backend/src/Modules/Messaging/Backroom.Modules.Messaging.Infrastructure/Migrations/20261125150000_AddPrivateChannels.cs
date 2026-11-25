using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Backroom.Modules.Messaging.Infrastructure.Migrations;

/// <summary>
/// Phase 2 step 7 (docs/contracts/phase-2.md, flow 1 and "Data"): what private channels and channel management need.
/// <list type="bullet">
/// <item><c>conversations.description</c> (channels; at most 250 characters, checked by the application, bounded
/// here in code points) and <c>owner_account_id</c> (private channels: the copy of the member row whose role is
/// <c>owner</c>, written in the same transaction).</item>
/// <item><c>conversations_private_open_idx</c>: the admins' count of open private channels, an index-only count.</item>
/// <item><c>conversations_creator_idx</c> and <c>conversation_members_added_by_idx</c>: the hourly brakes on new
/// channels and on member additions read the newest rows of one person.</item>
/// </list>
/// No table is added and no row is changed, so there is no data step and no new row-level security policy. The
/// indexes are built under the migration's lock, which is acceptable only while the tables are small (before
/// release); see the note on <c>AddChangeSequences</c>.
/// </summary>
[DbContext(typeof(MessagingDbContext))]
[Migration("20261125150000_AddPrivateChannels")]
internal sealed class AddPrivateChannels : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql("""
            SET LOCAL lock_timeout = '5s';
            SET LOCAL statement_timeout = '10min';

            ALTER TABLE messaging.conversations
                ADD COLUMN description text CONSTRAINT conversations_description_check CHECK (char_length(description) BETWEEN 1 AND 1000),
                -- Only a private channel has an owner.
                ADD COLUMN owner_account_id uuid CONSTRAINT conversations_owner_check CHECK (owner_account_id IS NULL OR kind = 'private_channel');

            CREATE INDEX conversations_private_open_idx ON messaging.conversations (org_id)
                WHERE kind = 'private_channel' AND archived_at IS NULL;

            CREATE INDEX conversations_creator_idx ON messaging.conversations (org_id, created_by, created_at)
                WHERE kind IN ('public_channel', 'private_channel');

            -- People added by somebody else; a person's own joins are not counted by the brake.
            CREATE INDEX conversation_members_added_by_idx ON messaging.conversation_members (org_id, added_by, joined_at)
                WHERE added_by <> account_id;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql("""
            SET LOCAL lock_timeout = '5s';

            DROP INDEX messaging.conversation_members_added_by_idx;
            DROP INDEX messaging.conversations_creator_idx;
            DROP INDEX messaging.conversations_private_open_idx;
            ALTER TABLE messaging.conversations DROP COLUMN owner_account_id, DROP COLUMN description;
            """);
    }
}
