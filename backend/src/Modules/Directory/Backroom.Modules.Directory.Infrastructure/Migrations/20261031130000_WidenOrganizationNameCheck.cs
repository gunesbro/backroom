using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Backroom.Modules.Directory.Infrastructure.Migrations;

/// <summary>
/// A name is at most 100 user-perceived characters (checked by the service); PostgreSQL counts code points,
/// so the column allows the service's code-point limit (four per character): 100 flag emoji are 200.
/// </summary>
[DbContext(typeof(DirectoryDbContext))]
[Migration("20261031130000_WidenOrganizationNameCheck")]
internal sealed class WidenOrganizationNameCheck : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql("""
            ALTER TABLE directory.organizations DROP CONSTRAINT organizations_name_check;
            ALTER TABLE directory.organizations ADD CONSTRAINT organizations_name_check CHECK (char_length(name) BETWEEN 1 AND 400);
            """);
    }
}
