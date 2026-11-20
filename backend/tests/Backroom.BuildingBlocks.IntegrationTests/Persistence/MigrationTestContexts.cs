using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Backroom.BuildingBlocks.Infrastructure.Persistence;

namespace Backroom.BuildingBlocks.IntegrationTests.Persistence;

/// <summary>A module whose migration creates an organization table with the policy.</summary>
public sealed class IsolatedModuleContext(DbContextOptions<IsolatedModuleContext> options) : DbContext(options)
{
    public const string Schema = "isolated_module";
}

/// <summary>A module whose migration forgets row-level security.</summary>
public sealed class LeakyModuleContext(DbContextOptions<LeakyModuleContext> options) : DbContext(options)
{
    public const string Schema = "leaky_module";
}

[DbContext(typeof(IsolatedModuleContext))]
[Migration("20261026000000_CreateItems")]
public sealed class CreateIsolatedItems : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql($"CREATE TABLE {IsolatedModuleContext.Schema}.items (id uuid PRIMARY KEY, org_id uuid NOT NULL)");
        migrationBuilder.EnableOrganizationRowLevelSecurity(IsolatedModuleContext.Schema, "items");
    }
}

[DbContext(typeof(LeakyModuleContext))]
[Migration("20261026000000_CreateItems")]
public sealed class CreateLeakyItems : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql($"CREATE TABLE {LeakyModuleContext.Schema}.items (id uuid PRIMARY KEY, org_id uuid NOT NULL)");
    }
}
