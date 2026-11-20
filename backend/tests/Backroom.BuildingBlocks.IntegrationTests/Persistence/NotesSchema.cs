using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Backroom.BuildingBlocks.Infrastructure.Messaging;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.Testing.Postgres;

namespace Backroom.BuildingBlocks.IntegrationTests.Persistence;

/// <summary>
/// An organization table and an outbox set up the way module migrations set theirs up: created by the
/// migrator, with the organization policy, and readable and writable (but not alterable) by the application role.
/// </summary>
public static class NotesSchema
{
    public const string Schema = "rls_test";

    public const string Table = "notes";

    private static readonly SemaphoreSlim s_lock = new(1, 1);
    private static bool s_created;

    /// <summary>Creates the table once per run and deletes its rows.</summary>
    public static async Task PrepareAsync(PostgresDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        await s_lock.WaitAsync();
        try
        {
            if (!s_created)
            {
                await database.CreateModuleSchemaAsync(Schema);
                await database.ExecuteAsMigratorAsync($"""
                    CREATE TABLE {Schema}.{Table} (id uuid PRIMARY KEY, org_id uuid NOT NULL, text text NOT NULL);
                    {OrganizationRowLevelSecurity.EnableSql(Schema, Table)}
                    {OutboxTable.CreateSql(Schema)}
                    {InboxTable.CreateSql(Schema)}
                    """);
                s_created = true;
            }
        }
        finally
        {
            s_lock.Release();
        }

        await database.ResetAsync(Schema);
    }

    /// <summary>Services as a host registers them, connecting as the application role.</summary>
    public static ServiceProvider CreateServices(PostgresDatabase database, int maxPoolSize = 10, string? connectionString = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDatabaseSession().Configure(options =>
        {
            options.ConnectionString = connectionString ?? database.ApplicationConnectionString(maxPoolSize);
            options.MaxPoolSize = maxPoolSize;
        });
        services.AddModuleDbContext<NotesDbContext>(Schema);
        services.AddOutbox();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    /// <summary>Inserts a note as superuser, bypassing row-level security.</summary>
    public static async Task<Guid> SeedAsync(PostgresDatabase database, Guid organizationId, string text)
    {
        ArgumentNullException.ThrowIfNull(database);
        var id = Guid.CreateVersion7();
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"INSERT INTO {Schema}.{Table} (id, org_id, text) VALUES ($1, $2, $3)", connection);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organizationId });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = text });
        await command.ExecuteNonQueryAsync();
        return id;
    }

    /// <summary>All notes, read as superuser.</summary>
    public static async Task<List<(Guid OrganizationId, string Text)>> ReadAllAsync(PostgresDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"SELECT org_id, text FROM {Schema}.{Table} ORDER BY text", connection);
        await using var reader = await command.ExecuteReaderAsync();
        var notes = new List<(Guid, string)>();
        while (await reader.ReadAsync())
        {
            notes.Add((reader.GetGuid(0), reader.GetString(1)));
        }

        return notes;
    }
}
