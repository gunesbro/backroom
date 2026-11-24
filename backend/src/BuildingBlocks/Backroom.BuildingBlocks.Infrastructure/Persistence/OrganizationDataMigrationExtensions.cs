using Microsoft.EntityFrameworkCore.Migrations;

namespace Backroom.BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// Data statements (<c>INSERT</c>, <c>UPDATE</c>, <c>DELETE</c>) in migrations of organization tables (docs/adr/0005,
/// "Data migrations"). Migrations run as the migrator, which owns the tables but has no <c>BYPASSRLS</c> and no
/// organization context. Row-level security is forced on the owner too, so a plain data statement sees no row and
/// silently does nothing. This helper lifts <c>FORCE</c> on the named tables, runs the statements and restores
/// <c>FORCE</c>, all in the migration's transaction: <c>ALTER TABLE</c> holds an <c>ACCESS EXCLUSIVE</c> lock until
/// commit, so no other session ever sees the table without <c>FORCE</c>, and a failure rolls the change back.
/// The statements are reviewed migration code and must keep every row in its organization.
/// <para>
/// The lock blocks every reader and writer of the tables until the migration commits, so a large backfill stalls
/// the service for its whole run: batch it (several migrations, each bounded) or run it off-hours. Waiting for the
/// lock gives up after <see cref="LockTimeout"/> (<c>SET LOCAL lock_timeout</c>, for the rest of the migration's
/// transaction), so the migration fails and can be retried instead of queueing every session behind it.
/// </para>
/// </summary>
public static class OrganizationDataMigrationExtensions
{
    /// <summary>The first line of every operation this helper emits; the architecture tests look for it.</summary>
    public const string Marker = "-- backroom: organization data migration";

    /// <summary>How long the migration waits for the tables' <c>ACCESS EXCLUSIVE</c> lock.</summary>
    public const string LockTimeout = "5s";

    /// <summary>
    /// Runs <paramref name="sql"/> as the table owner with row-level security not forced on
    /// <paramref name="tables"/> (in <paramref name="schema"/>). List every organization table the statements read
    /// or write; any other organization table still hides its rows.
    /// </summary>
    public static MigrationBuilder OrganizationDataSql(this MigrationBuilder migrationBuilder, string schema, IReadOnlyCollection<string> tables, string sql)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql(OrganizationDataSqlText(schema, tables, sql), suppressTransaction: false);
        return migrationBuilder;
    }

    /// <summary>The SQL <see cref="OrganizationDataSql"/> emits.</summary>
    public static string OrganizationDataSqlText(string schema, IReadOnlyCollection<string> tables, string sql)
    {
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        if (tables.Count == 0)
        {
            throw new ArgumentException("Name at least one table.", nameof(tables));
        }

        var names = tables.Select(table => OrganizationRowLevelSecurity.QualifiedName(schema, table)).ToList();
        var lift = string.Concat(names.Select(name => $"ALTER TABLE {name} NO FORCE ROW LEVEL SECURITY;\n"));
        var restore = string.Concat(names.Select(name => $"ALTER TABLE {name} FORCE ROW LEVEL SECURITY;\n"));
        var statements = sql.Trim();
        var terminator = statements.EndsWith(';') ? string.Empty : ";";
        return $"{Marker}\nSET LOCAL lock_timeout = '{LockTimeout}';\n{lift}{statements}{terminator}\n{restore}";
    }
}
