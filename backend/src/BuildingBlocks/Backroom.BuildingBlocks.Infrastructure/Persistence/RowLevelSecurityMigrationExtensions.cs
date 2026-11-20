using Microsoft.EntityFrameworkCore.Migrations;

namespace Backroom.BuildingBlocks.Infrastructure.Persistence;

public static class RowLevelSecurityMigrationExtensions
{
    /// <summary>
    /// Enables and forces row-level security on an organization table and adds the organization policy.
    /// Call it in the migration that creates the table.
    /// </summary>
    public static MigrationBuilder EnableOrganizationRowLevelSecurity(this MigrationBuilder migrationBuilder, string schema, string table)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql(OrganizationRowLevelSecurity.EnableSql(schema, table));
        return migrationBuilder;
    }
}
