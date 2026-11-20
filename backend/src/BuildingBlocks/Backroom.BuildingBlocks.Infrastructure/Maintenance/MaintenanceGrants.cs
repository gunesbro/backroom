using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Backroom.BuildingBlocks.Infrastructure.Maintenance;

/// <summary>
/// Grants for the maintenance role (docs/adr/0010). The role has no table privileges by default; the migration
/// of the module that owns a job grants exactly the columns the job needs. Identifiers are validated like
/// <see cref="OrganizationRowLevelSecurity"/> does, then quoted.
/// </summary>
public static class MaintenanceGrants
{
    private const int MaxColumns = 32;

    /// <summary>
    /// <c>GRANT SELECT (...)</c> and <c>GRANT UPDATE (...)</c> on the given columns. PostgreSQL needs
    /// <c>SELECT</c> on every column a query references, filters included, so list state and time columns
    /// only and never content columns. Either list may be empty, but not both.
    /// </summary>
    public static string GrantColumnsSql(string schema, string table, IReadOnlyCollection<string> selectColumns, IReadOnlyCollection<string> updateColumns)
    {
        ArgumentNullException.ThrowIfNull(selectColumns);
        ArgumentNullException.ThrowIfNull(updateColumns);
        if (selectColumns.Count == 0 && updateColumns.Count == 0)
        {
            throw new ArgumentException("Grant at least one column.", nameof(selectColumns));
        }

        var name = OrganizationRowLevelSecurity.QualifiedName(schema, table);
        var statements = new List<string>(2);
        if (selectColumns.Count > 0)
        {
            statements.Add($"GRANT SELECT ({ColumnList(selectColumns, nameof(selectColumns))}) ON {name} TO {DatabaseRoles.Maintenance};");
        }

        if (updateColumns.Count > 0)
        {
            statements.Add($"GRANT UPDATE ({ColumnList(updateColumns, nameof(updateColumns))}) ON {name} TO {DatabaseRoles.Maintenance};");
        }

        return string.Join('\n', statements);
    }

    /// <summary>
    /// <c>GRANT DELETE</c>, for a job that deletes whole rows (it also needs <c>SELECT</c> on its filter
    /// columns). List the <c>ON DELETE CASCADE</c> tables it can reach in the migration's comment: cascades run
    /// as the table owner, and every one must stay inside the owning module's schema.
    /// </summary>
    public static string GrantDeleteSql(string schema, string table) =>
        $"GRANT DELETE ON {OrganizationRowLevelSecurity.QualifiedName(schema, table)} TO {DatabaseRoles.Maintenance};";

    /// <summary>Removes every privilege the maintenance role has on the table (the migration's <c>Down</c>).</summary>
    public static string RevokeAllSql(string schema, string table) =>
        $"REVOKE ALL ON {OrganizationRowLevelSecurity.QualifiedName(schema, table)} FROM {DatabaseRoles.Maintenance};";

    /// <summary>Grants column privileges to the maintenance role. Call it in the migration of the job's module.</summary>
    public static MigrationBuilder GrantMaintenanceColumns(
        this MigrationBuilder migrationBuilder,
        string schema,
        string table,
        IReadOnlyCollection<string> selectColumns,
        IReadOnlyCollection<string> updateColumns)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql(GrantColumnsSql(schema, table, selectColumns, updateColumns));
        return migrationBuilder;
    }

    /// <summary>Grants <c>DELETE</c> on a table to the maintenance role. Call it in the migration of the job's module.</summary>
    public static MigrationBuilder GrantMaintenanceDelete(this MigrationBuilder migrationBuilder, string schema, string table)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql(GrantDeleteSql(schema, table));
        return migrationBuilder;
    }

    /// <summary>Revokes the maintenance role's privileges on a table. Call it in the migration's <c>Down</c>.</summary>
    public static MigrationBuilder RevokeMaintenanceGrants(this MigrationBuilder migrationBuilder, string schema, string table)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql(RevokeAllSql(schema, table));
        return migrationBuilder;
    }

    private static string ColumnList(IReadOnlyCollection<string> columns, string parameterName)
    {
        if (columns.Count > MaxColumns)
        {
            throw new ArgumentException($"At most {MaxColumns} columns.", parameterName);
        }

        foreach (var column in columns)
        {
            OrganizationRowLevelSecurity.ValidateIdentifier(column, parameterName);
        }

        return string.Join(", ", columns.Select(column => $"\"{column}\""));
    }
}
