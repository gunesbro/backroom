using Microsoft.EntityFrameworkCore.Migrations;

namespace Backroom.BuildingBlocks.Infrastructure.Messaging;

public static class OutboxMigrationExtensions
{
    /// <summary>Creates the module's outbox table with its indexes, row-level security and grants.</summary>
    public static MigrationBuilder CreateOutboxTable(this MigrationBuilder migrationBuilder, string schema)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql(OutboxTable.CreateSql(schema));
        return migrationBuilder;
    }

    /// <summary>Creates the module's inbox table with its index, row-level security and grants.</summary>
    public static MigrationBuilder CreateInboxTable(this MigrationBuilder migrationBuilder, string schema)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql(InboxTable.CreateSql(schema));
        return migrationBuilder;
    }
}
