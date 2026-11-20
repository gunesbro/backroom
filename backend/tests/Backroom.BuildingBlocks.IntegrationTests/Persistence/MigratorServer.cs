using System.Security.Cryptography;
using Npgsql;
using Backroom.Testing.Postgres;
using Testcontainers.PostgreSql;

namespace Backroom.BuildingBlocks.IntegrationTests.Persistence;

/// <summary>
/// A PostgreSQL server for the bootstrap and migrator tests, separate from the other tests' server
/// because the bootstrap creates the roles with passwords of its own.
/// </summary>
public sealed class MigratorServer : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(PostgresDatabase.Image).WithDatabase("cell").Build();

    public string MigratorPassword { get; } = NewPassword();

    public string ApplicationPassword { get; } = NewPassword();

    public string RelayPassword { get; } = NewPassword();

    /// <summary>The administrator's connection string.</summary>
    public string AdministratorConnectionString => _container.GetConnectionString();

    public string ConnectionStringFor(string role, string password) =>
        new NpgsqlConnectionStringBuilder(AdministratorConnectionString) { Username = role, Password = password }.ConnectionString;

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await _container.DisposeAsync();
    }

    private static string NewPassword() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
}
