using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Backroom.BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// Applies every registered module's migrations as the migrator role: prepares the module's schema
/// (owned by the migrator; the application and relay roles may use it, and the application role
/// gets data access to the tables created in it), runs the EF Core migrations, and checks
/// organization-scoped schemas with <see cref="RowLevelSecurityAudit"/>. A violation fails the run,
/// so a host never starts on a schema that breaks organization isolation.
/// </summary>
public sealed partial class DatabaseMigrator(
    IEnumerable<ModuleDatabase> modules,
    IOptions<DatabaseMigrationOptions> options,
    IServiceProvider services,
    ILogger<DatabaseMigrator> logger)
{
    // The migrator must not ignore row-level security (tables it creates would be owned by a role that
    // bypasses the policies), create roles, or use the server's files and programs.
    private const string MigratorRoleSql =
        "SELECT NOT (" + DatabaseRoleGuard.MemberOfSuperuserSql + " OR " + DatabaseRoleGuard.MemberOfBypassRlsSql
        + " OR " + DatabaseRoleGuard.HoldsServerPrivilegesSql + ")";

    // Functions get EXECUTE for PUBLIC by default; the migrator's functions must be granted explicitly.
    private const string RevokeFunctionDefaultsSql = "ALTER DEFAULT PRIVILEGES REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC";

    public async Task MigrateAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!string.IsNullOrEmpty(settings.BootstrapConnectionString))
        {
            await DatabaseBootstrapper.BootstrapAsync(
                settings.BootstrapConnectionString,
                settings.MigratorPassword!,
                settings.ApplicationPassword!,
                settings.RelayPassword!,
                cancellationToken);
            LogBootstrapped(logger);
        }

        var duplicate = modules.GroupBy(module => module.Schema, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"More than one module is registered for schema {duplicate.Key}.");
        }

        var migrator = new NpgsqlConnectionStringBuilder(settings.MigratorConnectionString) { IncludeErrorDetail = false }.ConnectionString;
        await using (var connection = new NpgsqlConnection(migrator))
        {
            await connection.OpenAsync(cancellationToken);
            await EnsureMigratorRoleAsync(connection, cancellationToken);
            await using (var revoke = new NpgsqlCommand(RevokeFunctionDefaultsSql, connection))
            {
                await revoke.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var module in modules)
            {
                await PrepareSchemaAsync(connection, module.Schema, cancellationToken);
            }
        }

        foreach (var module in modules)
        {
            await using (var context = module.CreateForMigration(services, migrator))
            {
                await context.Database.MigrateAsync(cancellationToken);
            }

            await ProtectHistoryAsync(migrator, module.Schema, cancellationToken);
            LogMigrated(logger, module.Schema);
        }

        await AuditAsync(migrator, cancellationToken);
    }

    private static async Task EnsureMigratorRoleAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(MigratorRoleSql, connection);
        if (await command.ExecuteScalarAsync(cancellationToken) is not true)
        {
            throw new InvalidOperationException(
                "Migrations must run as the migrator role, which has neither SUPERUSER nor BYPASSRLS and cannot create roles or use the server's files.");
        }
    }

    [SuppressMessage("Security", "CA2100", Justification = "The schema is a validated snake_case identifier and the role names are constants.")]
    private static async Task PrepareSchemaAsync(NpgsqlConnection connection, string schema, CancellationToken cancellationToken)
    {
        OrganizationRowLevelSecurity.ValidateIdentifier(schema, nameof(schema));
        var sql = $"""
            CREATE SCHEMA IF NOT EXISTS "{schema}";
            GRANT USAGE ON SCHEMA "{schema}" TO {DatabaseRoles.Application}, {DatabaseRoles.Relay};
            ALTER DEFAULT PRIVILEGES IN SCHEMA "{schema}" GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO {DatabaseRoles.Application};
            ALTER DEFAULT PRIVILEGES IN SCHEMA "{schema}" GRANT USAGE, SELECT ON SEQUENCES TO {DatabaseRoles.Application};
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // The history table is created by EF Core in the schema, so the default privileges gave the
    // application role write access to it: with that, injected SQL could mark a pending migration (for
    // example a privacy fix) as applied, or remove applied ones.
    [SuppressMessage("Security", "CA2100", Justification = "The schema is a validated snake_case identifier and the names are constants.")]
    private static async Task ProtectHistoryAsync(string migratorConnectionString, string schema, CancellationToken cancellationToken)
    {
        var table = OrganizationRowLevelSecurity.QualifiedName(schema, PersistenceServiceCollectionExtensions.MigrationsHistoryTable);
        await using var connection = new NpgsqlConnection(migratorConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"REVOKE ALL ON {table} FROM {DatabaseRoles.Application}, {DatabaseRoles.Relay}", connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task AuditAsync(string migratorConnectionString, CancellationToken cancellationToken)
    {
        var scoped = modules.Where(module => module.OrganizationScoped).Select(module => module.Schema).ToArray();
        if (scoped.Length == 0)
        {
            return;
        }

        await using var connection = new NpgsqlConnection(migratorConnectionString);
        await connection.OpenAsync(cancellationToken);
        var violations = await RowLevelSecurityAudit.FindViolationsAsync(connection, scoped, new HashSet<string>(StringComparer.Ordinal), cancellationToken);
        if (violations.Count > 0)
        {
            throw new InvalidOperationException("Organization isolation check failed after migrating: " + string.Join(" ", violations));
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Database roles bootstrapped (development).")]
    private static partial void LogBootstrapped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Migrations applied for schema {Schema}.")]
    private static partial void LogMigrated(ILogger logger, string schema);
}
