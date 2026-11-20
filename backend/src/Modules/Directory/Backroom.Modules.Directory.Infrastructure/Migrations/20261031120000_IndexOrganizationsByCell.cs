using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Backroom.Modules.Directory.Infrastructure.Migrations;

/// <summary>Organizations per cell: the load count when a new organization picks its cell, and the upstream scope check.</summary>
[DbContext(typeof(DirectoryDbContext))]
[Migration("20261031120000_IndexOrganizationsByCell")]
internal sealed class IndexOrganizationsByCell : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        // Partial: the load count skips failed organizations without visiting the table.
        migrationBuilder.Sql("CREATE INDEX organizations_cell_id_idx ON directory.organizations (cell_id) WHERE status <> 'failed';");
    }
}
