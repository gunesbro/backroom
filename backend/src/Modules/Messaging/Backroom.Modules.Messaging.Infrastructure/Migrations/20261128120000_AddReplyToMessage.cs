using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Backroom.Modules.Messaging.Infrastructure.Migrations;

/// <summary>
/// Phase 2 step 9 (docs/contracts/phase-2.md, flow 2 "Reply with quote" and "Data"): <c>messages.reply_to_message_id</c>,
/// the message a reply quotes. Nullable, no data step (no message is a reply yet). It is a plain reference without a foreign
/// key, on purpose: a deleted message keeps its row as a tombstone, so the reference never dangles in normal use, and a
/// foreign key would need an index on this column to keep a hard delete of messages (erasure of an organization) from
/// scanning the table once per row. A reference whose row is gone anyway reads as a deleted original. The same conversation
/// is checked by the application when the reply is sent. No index: nothing reads replies by their original in Phase 2 (the
/// quote is found from the reply, by primary key). A tombstone clears the column, so a deleted reply points at nothing.
/// </summary>
[DbContext(typeof(MessagingDbContext))]
[Migration("20261128120000_AddReplyToMessage")]
internal sealed class AddReplyToMessage : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql("""
            SET LOCAL lock_timeout = '5s';

            ALTER TABLE messaging.messages ADD COLUMN reply_to_message_id uuid;
            ALTER TABLE messaging.messages ADD CONSTRAINT messages_reply_to_check
                CHECK (reply_to_message_id IS NULL OR (reply_to_message_id <> id AND deleted_at IS NULL)) NOT VALID;
            ALTER TABLE messaging.messages VALIDATE CONSTRAINT messages_reply_to_check;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql("""
            SET LOCAL lock_timeout = '5s';

            ALTER TABLE messaging.messages DROP CONSTRAINT messages_reply_to_check;
            ALTER TABLE messaging.messages DROP COLUMN reply_to_message_id;
            """);
    }
}
