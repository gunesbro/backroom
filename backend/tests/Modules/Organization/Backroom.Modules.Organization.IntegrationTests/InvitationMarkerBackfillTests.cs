using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using AwesomeAssertions;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.Modules.Organization.Infrastructure;
using Backroom.Modules.Permissions.Infrastructure;
using Backroom.Testing.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Backroom.Modules.Organization.IntegrationTests;

/// <summary>
/// The erased-email marker backfill, run by the migrator exactly as a deployment runs it: as the table owner under
/// forced row-level security, without an organization context. Uses a database of its own, migrated to the last
/// migration before the marker, so rows of several organizations exist when the backfill runs.
/// </summary>
[Trait("Category", "Privacy")]
public sealed class InvitationMarkerBackfillTests(PostgresDatabase database) : IAsyncLifetime
{
    private const string LastMigrationBeforeTheMarker = "20261115150000_IndexInvitationJobs";

    private static readonly DateTimeOffset s_expiredAt = new(2001, 4, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_revokedAt = new(2001, 4, 5, 0, 0, 0, TimeSpan.Zero);

    private readonly string _database = $"backfill_{Guid.NewGuid():N}";

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() =>
        await database.ExecuteAsSuperuserAsync($"CREATE DATABASE {_database}; GRANT CREATE ON DATABASE {_database} TO {PostgresDatabase.MigratorRole};");

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await database.ExecuteAsSuperuserAsync($"DROP DATABASE IF EXISTS {_database} WITH (FORCE)");
    }

    [Fact]
    public async Task Migrate_WithErasedInvitationsInSeveralOrganizations_MarksEveryOneAsync()
    {
        await using var provider = BuildServices();
        await MigrateOrganizationToAsync(provider, LastMigrationBeforeTheMarker);
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var expired = new[] { await AddAsync(first, "expired", noEmail: true), await AddAsync(second, "expired", noEmail: true) };
        var revoked = new[] { await AddAsync(first, "revoked", noEmail: true), await AddAsync(second, "revoked", noEmail: true) };
        var kept = await AddAsync(second, "expired", noEmail: false);

        await provider.GetRequiredService<DatabaseMigrator>().MigrateAsync(CancellationToken);

        foreach (var invitation in expired)
        {
            (await ErasedAtAsync(invitation)).Should().Be(s_expiredAt, "the email was erased when the invitation expired");
        }

        foreach (var invitation in revoked)
        {
            (await ErasedAtAsync(invitation)).Should().Be(s_revokedAt, "the email was erased when the invitation was revoked");
        }

        (await ErasedAtAsync(kept)).Should().BeNull("it still holds its email");
        (await ForcedAsync()).Should().BeTrue("row-level security stays forced after the data migration");
    }

    private ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPermissionsDatabase();
        services.AddOrganizationDatabase();
        services.AddDatabaseMigrations(allowBootstrap: false).Configure(options => options.MigratorConnectionString = MigratorConnectionString);
        return services.BuildServiceProvider();
    }

    private async Task MigrateOrganizationToAsync(ServiceProvider provider, string target)
    {
        await ExecuteAsync($"CREATE SCHEMA {OrganizationDbContext.Schema} AUTHORIZATION {PostgresDatabase.MigratorRole}");
        var module = provider.GetServices<ModuleDatabase>().Single(candidate => candidate.Schema == OrganizationDbContext.Schema);
        await using var context = module.CreateForMigration(provider, MigratorConnectionString);
        await context.GetService<IMigrator>().MigrateAsync(target, CancellationToken);
    }

    private async Task<Guid> AddAsync(Guid organization, string status, bool noEmail)
    {
        await ExecuteAsync(
            "INSERT INTO organization.organizations (id, org_id, name, owner_account_id, version, created_at) VALUES ($1, $1, 'Acme', $2, 1, $3) ON CONFLICT DO NOTHING",
            organization, Guid.CreateVersion7(), s_expiredAt.AddYears(-1));
        var invitation = Guid.CreateVersion7();
        await ExecuteAsync(
            """
            INSERT INTO organization.invitations
                (id, org_id, email, role, salt, key_version, code_version, code_hash, status, created_by, created_at, expires_at, expired_at, revoked_at)
            VALUES ($1, $2, $3, 'member', $4, 1, 1, $5, $6, $7, $8, $9, $10, $11)
            """,
            invitation, organization, noEmail ? DBNull.Value : $"kept-{invitation:N}@example.test", RandomNumberGenerator.GetBytes(16),
            RandomNumberGenerator.GetBytes(32), status, Guid.CreateVersion7(), s_expiredAt.AddDays(-14), s_expiredAt.AddDays(-1),
            status == "expired" ? s_expiredAt : DBNull.Value, status == "revoked" ? s_revokedAt : DBNull.Value);
        return invitation;
    }

    private async Task<DateTimeOffset?> ErasedAtAsync(Guid invitation)
    {
        var value = await ScalarAsync("SELECT email_erased_at FROM organization.invitations WHERE id = $1", invitation);
        return value is DateTime moment ? new DateTimeOffset(moment.ToUniversalTime()) : null;
    }

    private async Task<bool> ForcedAsync() =>
        (bool)(await ScalarAsync("SELECT relforcerowsecurity FROM pg_catalog.pg_class WHERE oid = 'organization.invitations'::regclass"))!;

    private string MigratorConnectionString =>
        new NpgsqlConnectionStringBuilder(database.MigratorConnectionString) { Database = _database }.ConnectionString;

    private string SuperuserConnectionString =>
        new NpgsqlConnectionStringBuilder(database.SuperuserConnectionString) { Database = _database }.ConnectionString;

    [SuppressMessage("Security", "CA2100", Justification = "Test SQL is constant; values are parameters.")]
    private async Task<object?> ScalarAsync(string sql, params object[] values)
    {
        await using var connection = new NpgsqlConnection(SuperuserConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }

        return await command.ExecuteScalarAsync(CancellationToken);
    }

    [SuppressMessage("Security", "CA2100", Justification = "Test SQL is constant; values are parameters.")]
    private async Task ExecuteAsync(string sql, params object[] values)
    {
        await using var connection = new NpgsqlConnection(SuperuserConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }

        await command.ExecuteNonQueryAsync(CancellationToken);
    }
}
