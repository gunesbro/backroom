using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Npgsql;
using Respawn;
using Testcontainers.PostgreSql;
using Xunit;

namespace Backroom.Testing.Postgres;

/// <summary>
/// A PostgreSQL server in a container for integration tests, with the same roles as a real
/// deployment: <see cref="MigratorRole"/> owns the schemas and tables, and <see cref="ApplicationRole"/>
/// (no SUPERUSER, no BYPASSRLS, not an owner) is what services connect as. Share one instance per test
/// assembly (<c>[assembly: AssemblyFixture(typeof(PostgresDatabase))]</c>) and call
/// <see cref="ResetAsync"/> at the start of each test.
/// </summary>
public sealed class PostgresDatabase : IAsyncLifetime
{
    /// <summary>The same major and minor version as the AppHost's PostgreSQL container.</summary>
    public const string Image = "postgres:18.3";

    public const string DatabaseName = "backroom";

    public const string MigratorRole = "backroom_migrator";

    public const string ApplicationRole = "backroom_app";

    /// <summary>The outbox relay: BYPASSRLS, privileges only on outbox tables (granted by their migrations).</summary>
    public const string RelayRole = "backroom_relay";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(Image)
        .WithDatabase(DatabaseName)
        .Build();

    // Generated per run; the container is local and short-lived.
    private readonly string _migratorPassword = NewPassword();
    private readonly string _applicationPassword = NewPassword();
    private readonly string _relayPassword = NewPassword();
    private readonly SemaphoreSlim _respawnerLock = new(1, 1);
    private readonly Dictionary<string, Respawner> _respawners = new(StringComparer.Ordinal);

    /// <summary>Superuser connection: bypasses row-level security, so use it only to arrange and inspect data.</summary>
    public string SuperuserConnectionString => _container.GetConnectionString();

    public string MigratorConnectionString => ConnectionStringFor(MigratorRole, _migratorPassword);

    /// <summary>The application role's connection string, with a pool of <paramref name="maxPoolSize"/> connections.</summary>
    public string ApplicationConnectionString(int maxPoolSize = 10) =>
        new NpgsqlConnectionStringBuilder(ConnectionStringFor(ApplicationRole, _applicationPassword)) { MaxPoolSize = maxPoolSize }
            .ConnectionString;

    public string RelayConnectionString => ConnectionStringFor(RelayRole, _relayPassword);

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        // CREATE ROLE takes no parameters; the passwords are generated hex strings.
        await ExecuteAsSuperuserAsync($"""
            CREATE ROLE {MigratorRole} LOGIN PASSWORD '{_migratorPassword}' NOSUPERUSER NOBYPASSRLS NOCREATEROLE NOCREATEDB;
            CREATE ROLE {ApplicationRole} LOGIN PASSWORD '{_applicationPassword}' NOSUPERUSER NOBYPASSRLS NOCREATEROLE NOCREATEDB NOINHERIT;
            CREATE ROLE {RelayRole} LOGIN PASSWORD '{_relayPassword}' NOSUPERUSER BYPASSRLS NOCREATEROLE NOCREATEDB NOINHERIT;
            GRANT CREATE, CONNECT ON DATABASE {DatabaseName} TO {MigratorRole};
            GRANT CONNECT ON DATABASE {DatabaseName} TO {ApplicationRole};
            GRANT CONNECT ON DATABASE {DatabaseName} TO {RelayRole};
            REVOKE CREATE ON SCHEMA public FROM PUBLIC;
            """);
    }

    /// <summary>
    /// Creates <paramref name="schema"/> owned by the migrator and gives the application role data
    /// access (no DDL) to the tables the migrator creates in it.
    /// </summary>
    public Task CreateModuleSchemaAsync(string schema) =>
        ExecuteAsSuperuserAsync($"""
            CREATE SCHEMA IF NOT EXISTS {schema} AUTHORIZATION {MigratorRole};
            GRANT USAGE ON SCHEMA {schema} TO {ApplicationRole};
            GRANT USAGE ON SCHEMA {schema} TO {RelayRole};
            ALTER DEFAULT PRIVILEGES FOR ROLE {MigratorRole} IN SCHEMA {schema}
                GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO {ApplicationRole};
            """);

    /// <summary>
    /// Creates a login role (attributes such as <c>BYPASSRLS</c> appended to CREATE ROLE, then membership
    /// in <paramref name="memberOf"/>) and returns its connection string. For tests of misconfigured roles.
    /// </summary>
    public async Task<string> CreateLoginRoleAsync(string role, string attributes = "", params string[] memberOf)
    {
        ArgumentNullException.ThrowIfNull(memberOf);
        var password = NewPassword();
        var grants = string.Concat(memberOf.Select(parent => $"GRANT {parent} TO {role};"));
        await ExecuteAsSuperuserAsync($"""
            CREATE ROLE {role} LOGIN PASSWORD '{password}' {attributes};
            GRANT CONNECT ON DATABASE {DatabaseName} TO {role};
            {grants}
            """);
        return ConnectionStringFor(role, password);
    }

    /// <summary>Deletes every row in <paramref name="schemas"/> (as superuser, so row-level security does not hide rows).</summary>
    public async Task ResetAsync(params string[] schemas)
    {
        ArgumentNullException.ThrowIfNull(schemas);
        await using var connection = new NpgsqlConnection(SuperuserConnectionString);
        await connection.OpenAsync();

        var key = string.Join(',', schemas.Order(StringComparer.Ordinal));
        Respawner respawner;
        await _respawnerLock.WaitAsync();
        try
        {
            if (!_respawners.TryGetValue(key, out respawner!))
            {
                respawner = await Respawner.CreateAsync(connection, new RespawnerOptions
                {
                    DbAdapter = DbAdapter.Postgres,
                    SchemasToInclude = schemas,
                    TablesToIgnore = ["__ef_migrations_history"],
                });
                _respawners[key] = respawner;
            }
        }
        finally
        {
            _respawnerLock.Release();
        }

        await respawner.ResetAsync(connection);
    }

    [SuppressMessage("Security", "CA2100", Justification = "Test setup helper; callers pass SQL written in tests, never external input.")]
    public async Task ExecuteAsSuperuserAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(SuperuserConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    [SuppressMessage("Security", "CA2100", Justification = "Test setup helper; callers pass SQL written in tests, never external input.")]
    public async Task ExecuteAsMigratorAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(MigratorConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        _respawnerLock.Dispose();
        await _container.DisposeAsync();
    }

    private static string NewPassword() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    private string ConnectionStringFor(string role, string password) =>
        new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Username = role, Password = password }.ConnectionString;
}
