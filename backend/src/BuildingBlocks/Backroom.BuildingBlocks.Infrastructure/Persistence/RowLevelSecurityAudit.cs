using Npgsql;

namespace Backroom.BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// Checks the live schema against the isolation rules: every table in the given schemas has an
/// <c>org_id</c> column, row-level security is enabled and forced, and the only policy is the
/// organization policy for every command. An extra permissive policy would be OR-ed with it and
/// could expose other organizations' rows, so any other policy is reported too. Relations row-level
/// security cannot protect are reported as well: materialized views, foreign tables, and views that do
/// not run with the caller's permissions (<c>security_invoker</c>). The migrator runs it after every
/// migration of an organization-scoped module.
/// </summary>
public static class RowLevelSecurityAudit
{
    private const string TablesSql = """
        SELECT n.nspname, c.relname, c.relrowsecurity, c.relforcerowsecurity,
               EXISTS (SELECT 1 FROM pg_catalog.pg_attribute a
                       WHERE a.attrelid = c.oid AND a.attname = 'org_id' AND NOT a.attisdropped) AS has_org_id
        FROM pg_catalog.pg_class c
        JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
        WHERE c.relkind IN ('r', 'p') AND n.nspname = ANY($1)
        ORDER BY n.nspname, c.relname
        """;

    private const string OtherRelationsSql = """
        SELECT n.nspname, c.relname, c.relkind::text, coalesce(c.reloptions, '{}')
        FROM pg_catalog.pg_class c
        JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
        WHERE c.relkind IN ('v', 'm', 'f') AND n.nspname = ANY($1)
        ORDER BY n.nspname, c.relname
        """;

    private const string PoliciesSql = """
        SELECT schemaname, tablename, policyname, permissive, cmd, roles::text[], qual, with_check
        FROM pg_catalog.pg_policies
        WHERE schemaname = ANY($1)
        """;

    // How PostgreSQL prints OrganizationRowLevelSecurity.PolicyExpression back (pg_get_expr).
    private const string StoredPolicyExpression =
        "(org_id = (NULLIF(current_setting('app.org_id'::text, true), ''::text))::uuid)";

    /// <summary>
    /// Returns a description of every violation in <paramref name="schemas"/>. The EF Core migrations
    /// history tables are skipped, and so are tables named in <paramref name="exemptTables"/> (as
    /// <c>schema.table</c>); each exemption needs a documented reason.
    /// </summary>
    public static async Task<IReadOnlyList<string>> FindViolationsAsync(
        NpgsqlConnection connection,
        IReadOnlyCollection<string> schemas,
        IReadOnlySet<string> exemptTables,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(schemas);
        ArgumentNullException.ThrowIfNull(exemptTables);

        var schemaArray = schemas.ToArray();
        var policies = await ReadPoliciesAsync(connection, schemaArray, cancellationToken);
        var violations = new List<string>();

        await using var command = new NpgsqlCommand(TablesSql, connection);
        command.Parameters.Add(new NpgsqlParameter<string[]> { TypedValue = schemaArray });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var table = $"{reader.GetString(0)}.{reader.GetString(1)}";
            if (exemptTables.Contains(table)
                || string.Equals(reader.GetString(1), PersistenceServiceCollectionExtensions.MigrationsHistoryTable, StringComparison.Ordinal))
            {
                continue;
            }

            if (!reader.GetBoolean(4))
            {
                violations.Add($"{table} has no {OrganizationRowLevelSecurity.OrganizationColumn} column.");
            }

            if (!reader.GetBoolean(2) || !reader.GetBoolean(3))
            {
                violations.Add($"{table} does not have row-level security enabled and forced.");
            }

            var tablePolicies = policies.GetValueOrDefault(table) ?? [];
            if (!tablePolicies.Any(IsOrganizationPolicy))
            {
                violations.Add($"{table} lacks the {OrganizationRowLevelSecurity.PolicyName} policy for all commands.");
            }

            violations.AddRange(tablePolicies
                .Where(policy => !IsOrganizationPolicy(policy))
                .Select(policy => $"{table} has an unexpected policy {policy.Name}."));
        }

        await reader.DisposeAsync();
        violations.AddRange(await FindUnprotectableRelationsAsync(connection, schemaArray, exemptTables, cancellationToken));
        return violations;
    }

    private static async Task<List<string>> FindUnprotectableRelationsAsync(
        NpgsqlConnection connection, string[] schemas, IReadOnlySet<string> exemptTables, CancellationToken cancellationToken)
    {
        var violations = new List<string>();
        await using var command = new NpgsqlCommand(OtherRelationsSql, connection);
        command.Parameters.Add(new NpgsqlParameter<string[]> { TypedValue = schemas });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var relation = $"{reader.GetString(0)}.{reader.GetString(1)}";
            if (exemptTables.Contains(relation))
            {
                continue;
            }

            var kind = reader.GetString(2);
            var options = reader.GetFieldValue<string[]>(3);
            if (kind == "m")
            {
                violations.Add($"{relation} is a materialized view, which row-level security cannot protect.");
            }
            else if (kind == "f")
            {
                violations.Add($"{relation} is a foreign table, which row-level security cannot protect here.");
            }
            else if (!options.Any(option => option is "security_invoker=true" or "security_invoker=on" or "security_invoker=1"))
            {
                violations.Add($"{relation} is a view without security_invoker, so it reads with its owner's rights.");
            }
        }

        return violations;
    }

    private static bool IsOrganizationPolicy(Policy policy) =>
        string.Equals(policy.Name, OrganizationRowLevelSecurity.PolicyName, StringComparison.Ordinal)
        && string.Equals(policy.Permissive, "PERMISSIVE", StringComparison.Ordinal)
        && string.Equals(policy.Command, "ALL", StringComparison.Ordinal)
        && policy.Roles is ["public"]
        && string.Equals(policy.Using, StoredPolicyExpression, StringComparison.Ordinal)
        && string.Equals(policy.WithCheck, StoredPolicyExpression, StringComparison.Ordinal);

    private static async Task<Dictionary<string, List<Policy>>> ReadPoliciesAsync(
        NpgsqlConnection connection,
        string[] schemas,
        CancellationToken cancellationToken)
    {
        var policies = new Dictionary<string, List<Policy>>(StringComparer.Ordinal);
        await using var command = new NpgsqlCommand(PoliciesSql, connection);
        command.Parameters.Add(new NpgsqlParameter<string[]> { TypedValue = schemas });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var table = $"{reader.GetString(0)}.{reader.GetString(1)}";
            if (!policies.TryGetValue(table, out var list))
            {
                list = [];
                policies[table] = list;
            }

            list.Add(new Policy(
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetFieldValue<string[]>(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }

        return policies;
    }

    private sealed record Policy(string Name, string Permissive, string Command, string[] Roles, string? Using, string? WithCheck);
}
