using AwesomeAssertions;
using Backroom.BuildingBlocks.Infrastructure.Maintenance;
using Backroom.BuildingBlocks.IntegrationTests.Persistence;
using Backroom.Testing.Postgres;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Backroom.BuildingBlocks.IntegrationTests.Maintenance;

/// <summary>The shared check for dedicated cross-organization roles (docs/adr/0010), as the maintenance runner applies it.</summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Privacy")]
public sealed class MaintenanceRoleGuardTests(PostgresDatabase database) : IAsyncLifetime
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await NotesSchema.PrepareAsync(database);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task VerifyRole_WithTheMaintenanceRole_PassesAsync()
    {
        var verify = Verify(database.MaintenanceConnectionString);

        await verify.Should().NotThrowAsync();
    }

    [Fact]
    public async Task VerifyRole_WithASuperuser_ThrowsAsync()
    {
        var verify = Verify(database.SuperuserConnectionString);

        await verify.Should().ThrowAsync<InvalidOperationException>().WithMessage("*BYPASSRLS*");
    }

    [Fact]
    public async Task VerifyRole_WithARoleWithoutBypassRls_ThrowsAsync()
    {
        var verify = Verify(database.ApplicationConnectionString());

        await verify.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task VerifyRole_WithAnOwnerOfDatabaseObjects_ThrowsAsync()
    {
        var role = NewRoleName();
        var connectionString = await database.CreateLoginRoleAsync(role, "BYPASSRLS");
        await database.ExecuteAsSuperuserAsync($"CREATE SCHEMA {role}_schema AUTHORIZATION {role}");
        try
        {
            var verify = Verify(connectionString);

            await verify.Should().ThrowAsync<InvalidOperationException>();
        }
        finally
        {
            await database.ExecuteAsSuperuserAsync($"DROP SCHEMA {role}_schema");
        }
    }

    [Theory]
    [InlineData("BYPASSRLS CREATEROLE")]
    [InlineData("BYPASSRLS SUPERUSER")]
    public async Task VerifyRole_WithServerPrivilegeAttributes_ThrowsAsync(string attributes)
    {
        var connectionString = await database.CreateLoginRoleAsync(NewRoleName(), attributes);

        var verify = Verify(connectionString);

        await verify.Should().ThrowAsync<InvalidOperationException>();
    }

    [Theory]
    [InlineData("pg_read_server_files")]
    [InlineData("pg_write_server_files")]
    [InlineData("pg_execute_server_program")]
    public async Task VerifyRole_WithAServerPrivilegeRole_ThrowsAsync(string serverRole)
    {
        var connectionString = await database.CreateLoginRoleAsync(NewRoleName(), "BYPASSRLS", serverRole);

        var verify = Verify(connectionString);

        await verify.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task VerifyRole_WithARoleThatIsAMemberOfAnotherRole_ThrowsAsync()
    {
        var parent = NewRoleName();
        await database.ExecuteAsSuperuserAsync($"CREATE ROLE {parent} NOLOGIN");
        var connectionString = await database.CreateLoginRoleAsync(NewRoleName(), "BYPASSRLS", parent);

        var verify = Verify(connectionString);

        await verify.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task VerifyRole_WithARoleThatHasMembers_ThrowsAsync()
    {
        var role = NewRoleName();
        var connectionString = await database.CreateLoginRoleAsync(role, "BYPASSRLS");
        await database.ExecuteAsSuperuserAsync($"CREATE ROLE {NewRoleName()} NOLOGIN IN ROLE {role}");

        var verify = Verify(connectionString);

        await verify.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task MaintenanceRole_WithoutGrants_CannotReadAnyTableAsync()
    {
        await using var connection = new NpgsqlConnection(database.MaintenanceConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM rls_test.outbox", connection);

        var read = () => command.ExecuteScalarAsync(CancellationToken);

        (await read.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501");
    }

    private static string NewRoleName() => $"maint_{Guid.CreateVersion7().ToString("N")[^12..]}";

    private static Func<Task> Verify(string connectionString)
    {
        var options = new MaintenanceJobOptions { ConnectionString = connectionString };
        var runner = new MaintenanceJobRunner([new NoJob()], Options.Create(options), TimeProvider.System, NullLoggerFactory.Instance);
        return async () =>
        {
            await using (runner)
            {
                await runner.VerifyRoleAsync(CancellationToken);
            }
        };
    }

    private sealed class NoJob : IMaintenanceJob
    {
        public string Name => "rls_test.nothing";

        public TimeSpan Interval => TimeSpan.FromMinutes(1);

        public int BatchSize => 1;

        public Task<int> RunBatchAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken) => Task.FromResult(0);
    }
}
