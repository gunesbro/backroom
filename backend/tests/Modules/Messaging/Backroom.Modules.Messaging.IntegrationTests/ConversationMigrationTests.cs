using System.Diagnostics.CodeAnalysis;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.Modules.Messaging.Infrastructure;
using Backroom.Testing.Postgres;

namespace Backroom.Modules.Messaging.IntegrationTests;

/// <summary>
/// The migration <c>RenameChannelsToConversations</c> on Phase 1 data, in a database of its own (the other tests
/// share the assembly's database at the latest migration): Up keeps every channel and message and makes the creators
/// and authors members; Down restores Phase 1's schema and data, and refuses while private conversations exist.
/// </summary>
public sealed class ConversationMigrationTests(PostgresDatabase database) : IAsyncLifetime
{
    private const string Phase1Migration = "20261124120000_CreateChannelsAndMessages";
    private const string BeforeChangeSequences = "20261125090000_AddConversationLastMessageAt";

    private static readonly Guid s_orgA = Guid.CreateVersion7();
    private static readonly Guid s_orgB = Guid.CreateVersion7();
    private static readonly Guid s_alice = Guid.CreateVersion7();
    private static readonly Guid s_bob = Guid.CreateVersion7();
    private static readonly Guid s_carol = Guid.CreateVersion7();
    private static readonly Guid s_general = Guid.CreateVersion7();
    private static readonly Guid s_archived = Guid.CreateVersion7();
    private static readonly Guid s_elsewhere = Guid.CreateVersion7();

    private readonly string _name = "migration_" + Guid.NewGuid().ToString("N")[..12];
    private ServiceProvider _services = null!;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await database.ExecuteAsSuperuserAsync($"""
            CREATE DATABASE {_name};
            """);
        await database.ExecuteAsSuperuserAsync($"""
            GRANT CREATE, CONNECT ON DATABASE {_name} TO {PostgresDatabase.MigratorRole};
            GRANT CONNECT ON DATABASE {_name} TO {PostgresDatabase.ApplicationRole}, {PostgresDatabase.RelayRole}, {PostgresDatabase.MaintenanceRole};
            """);
        await ExecuteAsSuperuserAsync("REVOKE CREATE ON SCHEMA public FROM PUBLIC");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMessagingDatabase();
        services.AddDatabaseMigrations(allowBootstrap: false).Configure(options => options.MigratorConnectionString = InDatabase(database.MigratorConnectionString));
        _services = services.BuildServiceProvider();

        // The schema at the latest migration, as a deployment has it, then back to Phase 1 (Down) with Phase 1's data.
        await _services.GetRequiredService<DatabaseMigrator>().MigrateAsync(CancellationToken);
        await MigrateToAsync(Phase1Migration);
        await ExecuteAsSuperuserAsync($"""
            INSERT INTO messaging.channels (id, org_id, name, name_key, created_by, created_at, archived_at, last_seq) VALUES
                ('{s_general}', '{s_orgA}', 'General', 'general', '{s_alice}', '2026-11-01T10:00:00Z', NULL, 3),
                ('{s_archived}', '{s_orgA}', 'Old', 'old', '{s_bob}', '2026-11-02T10:00:00Z', '2026-11-03T10:00:00Z', 0),
                ('{s_elsewhere}', '{s_orgB}', 'General', 'general', '{s_carol}', '2026-11-04T10:00:00Z', NULL, 1);
            INSERT INTO messaging.messages (id, org_id, channel_id, seq, author_account_id, client_message_id, text, blocks, created_at) VALUES
                ('{Guid.CreateVersion7()}', '{s_orgA}', '{s_general}', 1, '{s_bob}', '{Guid.NewGuid()}', 'one', '[]', '2026-11-05T10:00:00Z'),
                ('{Guid.CreateVersion7()}', '{s_orgA}', '{s_general}', 2, '{s_bob}', '{Guid.NewGuid()}', 'two', '[]', '2026-11-06T10:00:00Z'),
                ('{Guid.CreateVersion7()}', '{s_orgA}', '{s_general}', 3, '{s_alice}', '{Guid.NewGuid()}', 'three', '[]', '2026-11-07T10:00:00Z'),
                ('{Guid.CreateVersion7()}', '{s_orgB}', '{s_elsewhere}', 1, '{s_carol}', '{Guid.NewGuid()}', 'four', '[]', '2026-11-08T10:00:00Z');
            """);
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        await database.ExecuteAsSuperuserAsync($"DROP DATABASE IF EXISTS {_name} WITH (FORCE)");
    }

    [Fact]
    public async Task Up_KeepsPhase1DataAndMakesCreatorsAndAuthorsMembersAsync()
    {
        await MigrateToAsync(null);

        (await QueryAsync("SELECT id, org_id, kind, name, name_key, created_by, change_seq, archived_at IS NOT NULL, member_count FROM messaging.conversations ORDER BY created_at"))
            .Should().BeEquivalentTo(
                new object?[][]
                {
                    [s_general, s_orgA, "public_channel", "General", "general", s_alice, 3L, false, 2],
                    [s_archived, s_orgA, "public_channel", "Old", "old", s_bob, 0L, true, 1],
                    [s_elsewhere, s_orgB, "public_channel", "General", "general", s_carol, 1L, false, 1],
                },
                options => options.WithStrictOrdering());
        (await QueryAsync("SELECT conversation_id, count(*) FROM messaging.messages GROUP BY conversation_id ORDER BY count(*) DESC"))
            .Should().BeEquivalentTo(new object?[][] { [s_general, 3L], [s_elsewhere, 1L] }, options => options.WithStrictOrdering());
        (await QueryAsync(
            """
            SELECT org_id, conversation_id, account_id, role, joined_at, joined_seq, added_by, left_at
            FROM messaging.conversation_members ORDER BY conversation_id, joined_at
            """))
            .Should().BeEquivalentTo(
                new object?[][]
                {
                    [s_orgA, s_general, s_alice, "member", At("2026-11-01T10:00:00Z"), 3L, s_alice, null],
                    [s_orgA, s_general, s_bob, "member", At("2026-11-05T10:00:00Z"), 3L, s_bob, null],
                    [s_orgA, s_archived, s_bob, "member", At("2026-11-02T10:00:00Z"), 0L, s_bob, null],
                    [s_orgB, s_elsewhere, s_carol, "member", At("2026-11-04T10:00:00Z"), 1L, s_carol, null],
                });
        (await QueryAsync(
            """
            SELECT relname, relrowsecurity, relforcerowsecurity FROM pg_class
            WHERE relnamespace = 'messaging'::regnamespace AND relname IN ('conversations', 'messages', 'conversation_members') ORDER BY relname
            """))
            .Should().BeEquivalentTo(
                new object?[][] { ["conversation_members", true, true], ["conversations", true, true], ["messages", true, true] },
                options => options.WithStrictOrdering(),
                "row-level security stays forced after the data steps");

        // A second run is a no-op that ends with the isolation audit of every table in the schema.
        await _services.GetRequiredService<DatabaseMigrator>().MigrateAsync(CancellationToken);
    }

    [Fact]
    public async Task Down_RestoresPhase1SchemaAndDataAsync()
    {
        await MigrateToAsync(null);

        await MigrateToAsync(Phase1Migration);

        (await QueryAsync("SELECT id, name, name_key, last_seq, archived_at IS NOT NULL FROM messaging.channels ORDER BY created_at"))
            .Should().BeEquivalentTo(
                new object?[][] { [s_general, "General", "general", 3L, false], [s_archived, "Old", "old", 0L, true], [s_elsewhere, "General", "general", 1L, false] },
                options => options.WithStrictOrdering());
        (await QueryAsync("SELECT count(*) FROM messaging.messages WHERE channel_id = '" + s_general + "'")).Should().BeEquivalentTo(new object?[][] { [3L] });
        (await QueryAsync("SELECT count(*) FROM pg_class WHERE relnamespace = 'messaging'::regnamespace AND relname IN ('conversations', 'conversation_members')"))
            .Should().BeEquivalentTo(new object?[][] { [0L] });
        (await QueryAsync("SELECT relforcerowsecurity FROM pg_class WHERE relnamespace = 'messaging'::regnamespace AND relname = 'channels'"))
            .Should().BeEquivalentTo(new object?[][] { [true] });
    }

    [Fact]
    public async Task Down_WhenAPrivateConversationExists_IsRefusedAndKeepsEverythingAsync()
    {
        await MigrateToAsync(null);
        var secret = Guid.CreateVersion7();
        await ExecuteAsSuperuserAsync($"""
            INSERT INTO messaging.conversations (id, org_id, kind, name, name_key, created_by, created_at)
            VALUES ('{secret}', '{s_orgA}', 'private_channel', 'Secret', 'secret', '{s_bob}', now());
            """);

        var failure = await Assert.ThrowsAsync<PostgresException>(() => MigrateToAsync(Phase1Migration));

        failure.MessageText.Should().Contain("would make them public");
        (await QueryAsync("SELECT count(*) FROM messaging.conversations")).Should().BeEquivalentTo(new object?[][] { [4L] });
        (await QueryAsync("SELECT count(*) FROM messaging.conversation_members")).Should().BeEquivalentTo(new object?[][] { [4L] });
    }

    [Fact]
    public async Task ChangeSequences_Up_SplitsLastSeqAndBackfillsFromTheMessages_AndDownRestoresItAsync()
    {
        await MigrateToAsync(BeforeChangeSequences);
        // A lost idempotent race left the counter ahead of the newest message.
        await ExecuteAsSuperuserAsync($"UPDATE messaging.conversations SET last_seq = 5 WHERE id = '{s_general}'");

        await MigrateToAsync(null);

        (await QueryAsync("SELECT id, change_seq, last_message_seq FROM messaging.conversations ORDER BY created_at"))
            .Should().BeEquivalentTo(
                new object?[][] { [s_general, 5L, 3L], [s_archived, 0L, 0L], [s_elsewhere, 1L, 1L] },
                options => options.WithStrictOrdering());
        (await QueryAsync("SELECT count(*), count(*) FILTER (WHERE version_seq = seq) FROM messaging.messages"))
            .Should().BeEquivalentTo(new object?[][] { [4L, 4L] });
        (await QueryAsync("SELECT relname, relforcerowsecurity FROM pg_class WHERE relnamespace = 'messaging'::regnamespace AND relname IN ('conversations', 'messages') ORDER BY relname"))
            .Should().BeEquivalentTo(new object?[][] { ["conversations", true], ["messages", true] }, options => options.WithStrictOrdering());

        await MigrateToAsync(BeforeChangeSequences);

        (await QueryAsync("SELECT id, last_seq FROM messaging.conversations ORDER BY created_at"))
            .Should().BeEquivalentTo(
                new object?[][] { [s_general, 5L], [s_archived, 0L], [s_elsewhere, 1L] },
                options => options.WithStrictOrdering());
        (await QueryAsync("SELECT count(*) FROM information_schema.columns WHERE table_schema = 'messaging' AND column_name IN ('version_seq', 'last_message_seq', 'change_seq')"))
            .Should().BeEquivalentTo(new object?[][] { [0L] });
    }

    [Fact]
    public async Task ChangeSequences_Down_WhenATombstoneExists_IsRefusedAndKeepsEverythingAsync()
    {
        await MigrateToAsync(null);
        await ExecuteAsSuperuserAsync($"""
            UPDATE messaging.conversations SET change_seq = 4 WHERE id = '{s_general}';
            UPDATE messaging.messages SET version_seq = 4, deleted_at = now(), text = NULL, blocks = NULL WHERE conversation_id = '{s_general}' AND seq = 2;
            """);

        var failure = await Assert.ThrowsAsync<PostgresException>(() => MigrateToAsync(BeforeChangeSequences));

        failure.MessageText.Should().Contain("cannot be removed");
        (await QueryAsync("SELECT count(*) FROM messaging.messages WHERE deleted_at IS NOT NULL AND version_seq = 4")).Should().BeEquivalentTo(new object?[][] { [1L] });
    }

    // Npgsql reads timestamptz as a UTC DateTime.
    private static DateTime At(string value) =>
        DateTime.Parse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);

    private string InDatabase(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Database = _name, Pooling = false }.ConnectionString;

    /// <summary>Migrates the messaging schema to <paramref name="target"/> (the latest when <see langword="null"/>) as the migrator role.</summary>
    private async Task MigrateToAsync(string? target)
    {
        var module = _services.GetServices<ModuleDatabase>().Single(candidate => candidate.Schema == "messaging");
        await using var context = module.CreateForMigration(_services, InDatabase(database.MigratorConnectionString));
        await context.GetService<IMigrator>().MigrateAsync(target, CancellationToken);
    }

    [SuppressMessage("Security", "CA2100", Justification = "The SQL comes from the tests.")]
    private async Task ExecuteAsSuperuserAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(InDatabase(database.SuperuserConnectionString));
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(CancellationToken);
    }

    [SuppressMessage("Security", "CA2100", Justification = "The SQL comes from the tests.")]
    private async Task<List<object?[]>> QueryAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(InDatabase(database.SuperuserConnectionString));
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(CancellationToken);
        var rows = new List<object?[]>();
        while (await reader.ReadAsync(CancellationToken))
        {
            var values = new object?[reader.FieldCount];
            for (var index = 0; index < values.Length; index++)
            {
                values[index] = reader.IsDBNull(index) ? null : reader.GetValue(index);
            }

            rows.Add(values);
        }

        return rows;
    }
}
