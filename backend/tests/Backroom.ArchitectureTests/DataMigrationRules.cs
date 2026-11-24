using System.Reflection;
using System.Text.RegularExpressions;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Backroom.ArchitectureTests;

/// <summary>
/// Data statements in migrations go through <see cref="OrganizationDataMigrationExtensions.OrganizationDataSql"/>
/// (docs/adr/0005, "Data migrations"). The migrator runs under forced row-level security without an organization
/// context, so a plain <c>INSERT</c>, <c>UPDATE</c>, <c>DELETE</c> or <c>MERGE</c> on an organization table sees no
/// row and silently does nothing.
/// </summary>
public static partial class DataMigrationRules
{
    /// <summary>
    /// Applied migrations written before the helper, by migration id. Never edited; each one names the migration
    /// that corrects it.
    /// </summary>
    private static readonly Dictionary<string, string> s_superseded = new(StringComparer.Ordinal)
    {
        ["20261124100000_EraseInvitationEmailsByMarker"] = "its backfill is repeated by 20261124130000_BackfillInvitationErasedMarkers",
    };

    public static IReadOnlyList<string> FindViolations(IEnumerable<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        var violations = new List<string>();
        foreach (var type in assemblies.SelectMany(assembly => assembly.GetTypes()))
        {
            if (type.IsAbstract || !typeof(Migration).IsAssignableFrom(type))
            {
                continue;
            }

            var id = type.GetCustomAttribute<MigrationAttribute>()?.Id ?? type.FullName!;
            if (s_superseded.ContainsKey(id))
            {
                continue;
            }

            var migration = (Migration)Activator.CreateInstance(type, nonPublic: true)!;
            violations.AddRange(FindViolations(id + " (Up)", migration.UpOperations));
            violations.AddRange(FindViolations(id + " (Down)", DownOperations(migration)));
        }

        return violations;
    }

    // A migration that cannot be reverted leaves Down unimplemented, which throws.
    private static IReadOnlyList<MigrationOperation> DownOperations(Migration migration)
    {
        try
        {
            return migration.DownOperations;
        }
        catch (NotSupportedException)
        {
            return [];
        }
    }

    public static IEnumerable<string> FindViolations(string name, IEnumerable<MigrationOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        foreach (var operation in operations)
        {
            switch (operation)
            {
                case InsertDataOperation or UpdateDataOperation or DeleteDataOperation:
                    yield return $"{name} uses {operation.GetType().Name}; write the statement with OrganizationDataSql.";
                    break;
                case SqlOperation sql when ContainsDataStatement(sql.Sql)
                    && !sql.Sql.StartsWith(OrganizationDataMigrationExtensions.Marker, StringComparison.Ordinal):
                    yield return $"{name} runs a data statement outside OrganizationDataSql; under forced row-level security it changes no row.";
                    break;
            }
        }
    }

    /// <summary>Whether the SQL holds an INSERT, UPDATE, DELETE or MERGE statement (comments ignored).</summary>
    public static bool ContainsDataStatement(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        var code = CommentPattern().Replace(sql, " ");
        return DataStatementPattern().IsMatch(code);
    }

    [GeneratedRegex(@"--[^\n]*|/\*.*?\*/", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex CommentPattern();

    // UPDATE <table> [[AS] alias] SET, never GRANT UPDATE (...), FOR UPDATE or ON UPDATE.
    [GeneratedRegex(
        @"\bINSERT\s+INTO\b|\bDELETE\s+FROM\b|\bMERGE\s+INTO\b|\bUPDATE\s+(ONLY\s+)?[""\w.]+(\s+(AS\s+)?\w+)?\s+SET\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DataStatementPattern();
}
