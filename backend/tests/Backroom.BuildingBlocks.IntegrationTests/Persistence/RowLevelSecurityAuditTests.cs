using AwesomeAssertions;
using Npgsql;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.Testing.Postgres;

namespace Backroom.BuildingBlocks.IntegrationTests.Persistence;

[Trait("Category", "Privacy")]
[Collection(PostgresCollection.Name)]
public sealed class RowLevelSecurityAuditTests(PostgresDatabase database)
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FindViolations_WhenTablesBreakTheRules_ReportsEachAsync()
    {
        // A fresh schema per run, so the test does not depend on others.
        var schema = "audit_" + Guid.CreateVersion7().ToString("N")[^12..];
        await database.CreateModuleSchemaAsync(schema);
        await database.ExecuteAsMigratorAsync($"""
            CREATE TABLE {schema}.no_rls (id uuid PRIMARY KEY, org_id uuid NOT NULL);
            CREATE TABLE {schema}.not_forced (id uuid PRIMARY KEY, org_id uuid NOT NULL);
            ALTER TABLE {schema}.not_forced ENABLE ROW LEVEL SECURITY;
            CREATE POLICY {OrganizationRowLevelSecurity.PolicyName} ON {schema}.not_forced
                USING ({OrganizationRowLevelSecurity.PolicyExpression}) WITH CHECK ({OrganizationRowLevelSecurity.PolicyExpression});
            CREATE TABLE {schema}.no_org (id uuid PRIMARY KEY);
            CREATE TABLE {schema}.extra_policy (id uuid PRIMARY KEY, org_id uuid NOT NULL);
            {OrganizationRowLevelSecurity.EnableSql(schema, "extra_policy")}
            CREATE POLICY open_read ON {schema}.extra_policy FOR SELECT USING (true);
            CREATE TABLE {schema}.weak_policy (id uuid PRIMARY KEY, org_id uuid NOT NULL);
            ALTER TABLE {schema}.weak_policy ENABLE ROW LEVEL SECURITY;
            ALTER TABLE {schema}.weak_policy FORCE ROW LEVEL SECURITY;
            CREATE POLICY {OrganizationRowLevelSecurity.PolicyName} ON {schema}.weak_policy USING (true);
            CREATE TABLE {schema}.good (id uuid PRIMARY KEY, org_id uuid NOT NULL);
            {OrganizationRowLevelSecurity.EnableSql(schema, "good")}
            CREATE TABLE {schema}.{PersistenceServiceCollectionExtensions.MigrationsHistoryTable} (migration_id text PRIMARY KEY);
            CREATE TABLE {schema}.exempt (id uuid PRIMARY KEY);
            CREATE VIEW {schema}.owner_view AS SELECT id, org_id FROM {schema}.good;
            CREATE VIEW {schema}.invoker_view WITH (security_invoker = true) AS SELECT id, org_id FROM {schema}.good;
            CREATE MATERIALIZED VIEW {schema}.cached AS SELECT id, org_id FROM {schema}.good;
            """);
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(CancellationToken);

        var violations = await RowLevelSecurityAudit.FindViolationsAsync(
            connection, [schema], new HashSet<string>(StringComparer.Ordinal) { $"{schema}.exempt" }, CancellationToken);

        violations.Should().BeEquivalentTo(
        [
            $"{schema}.extra_policy has an unexpected policy open_read.",
            $"{schema}.no_org has no org_id column.",
            $"{schema}.no_org does not have row-level security enabled and forced.",
            $"{schema}.no_org lacks the org_isolation policy for all commands.",
            $"{schema}.no_rls does not have row-level security enabled and forced.",
            $"{schema}.no_rls lacks the org_isolation policy for all commands.",
            $"{schema}.not_forced does not have row-level security enabled and forced.",
            $"{schema}.weak_policy lacks the org_isolation policy for all commands.",
            $"{schema}.weak_policy has an unexpected policy org_isolation.",
            $"{schema}.cached is a materialized view, which row-level security cannot protect.",
            $"{schema}.owner_view is a view without security_invoker, so it reads with its owner's rights.",
        ]);
    }
}
