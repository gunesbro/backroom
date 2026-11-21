using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Backroom.BuildingBlocks.Infrastructure.Persistence;

namespace Backroom.BuildingBlocks.IntegrationTests.Persistence;

/// <summary>The development bootstrap and the migrator, on a server of their own.</summary>
[Trait("Category", "Privacy")]
public sealed class DatabaseMigratorTests(MigratorServer server) : IClassFixture<MigratorServer>
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Bootstrap_WhenRunConcurrently_CreatesRolesThatCannotBypassIsolationAsync()
    {
        await Task.WhenAll(BootstrapAsync(), BootstrapAsync(), BootstrapAsync());

        var roles = await QueryAsync(
            "SELECT rolname || ':' || rolsuper || ':' || rolbypassrls || ':' || rolcreaterole || ':' || rolcreatedb FROM pg_roles WHERE rolname LIKE 'backroom_%' ORDER BY 1");

        roles.Should().Equal(
            "backroom_app:false:false:false:false",
            "backroom_maintenance:false:true:false:false",
            "backroom_migrator:false:false:false:false",
            "backroom_relay:false:true:false:false");
    }

    [Theory]
    [InlineData("SELECT has_database_privilege('backroom_app', current_database(), 'CREATE')::text")]
    [InlineData("SELECT has_database_privilege('backroom_app', current_database(), 'TEMP')::text")]
    [InlineData("SELECT has_database_privilege('public', current_database(), 'CONNECT')::text")]
    [InlineData("SELECT has_schema_privilege('backroom_app', 'public', 'CREATE')::text")]
    public async Task Bootstrap_WhenDone_LeavesNoExtraPrivilegesAsync(string privilegeQuery)
    {
        await BootstrapAsync();

        (await QueryAsync(privilegeQuery)).Should().Equal("false");
    }

    [Fact]
    public async Task Migrate_WhenModulesAreIsolated_CreatesTablesOwnedByTheMigratorAsync()
    {
        await using var services = CreateServices(bootstrap: true, modules => modules.AddModuleDbContext<IsolatedModuleContext>(IsolatedModuleContext.Schema));

        await services.GetRequiredService<DatabaseMigrator>().MigrateAsync(CancellationToken);
        await services.GetRequiredService<DatabaseMigrator>().MigrateAsync(CancellationToken);

        (await QueryAsync("SELECT tableowner FROM pg_tables WHERE schemaname = 'isolated_module' AND tablename = 'items'")).Should().Equal("backroom_migrator");
        (await QueryAsync("SELECT has_table_privilege('backroom_app', 'isolated_module.items', 'INSERT')::text")).Should().Equal("true");
        (await QueryAsync("SELECT has_table_privilege('backroom_app', 'isolated_module.items', 'TRUNCATE')::text")).Should().Equal("false");
        (await QueryAsync("SELECT has_schema_privilege('backroom_app', 'isolated_module', 'CREATE')::text")).Should().Equal("false");
    }

    [Theory]
    [InlineData("backroom_app", "SELECT")]
    [InlineData("backroom_app", "INSERT")]
    [InlineData("backroom_app", "DELETE")]
    [InlineData("backroom_relay", "SELECT")]
    [InlineData("backroom_maintenance", "SELECT")]
    public async Task Migrate_WhenDone_KeepsTheMigrationHistoryFromServiceRolesAsync(string role, string privilege)
    {
        // Regression: the application role could write the history and so skip pending migrations.
        await using var services = CreateServices(bootstrap: true, modules => modules.AddModuleDbContext<IsolatedModuleContext>(IsolatedModuleContext.Schema));
        await services.GetRequiredService<DatabaseMigrator>().MigrateAsync(CancellationToken);

        (await QueryAsync($"SELECT has_table_privilege('{role}', 'isolated_module.__ef_migrations_history', '{privilege}')::text")).Should().Equal("false");
    }

    [Fact]
    public async Task Bootstrap_ForTheMaintenanceRole_SetsTheTimeoutsOnTheRoleAsync()
    {
        await BootstrapAsync();

        (await QueryAsync("SELECT unnest(rolconfig) FROM pg_roles WHERE rolname = 'backroom_maintenance' ORDER BY 1")).Should().Equal(
            $"idle_in_transaction_session_timeout={DatabaseBootstrapper.MaintenanceIdleInTransactionTimeout}",
            $"lock_timeout={DatabaseBootstrapper.MaintenanceLockTimeout}",
            $"statement_timeout={DatabaseBootstrapper.MaintenanceStatementTimeout}");
    }

    [Fact]
    public async Task Bootstrap_ForTheMaintenanceRole_AppliesTheTimeoutsToItsSessionsAsync()
    {
        await BootstrapAsync();
        await using var connection = new NpgsqlConnection(server.ConnectionStringFor(DatabaseRoles.Maintenance, server.MaintenancePassword));
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand("SELECT current_setting('statement_timeout') || ',' || current_setting('lock_timeout') || ',' || current_setting('idle_in_transaction_session_timeout')", connection);

        (await command.ExecuteScalarAsync(CancellationToken)).Should().Be("30s,5s,1min");
    }

    [Fact]
    public async Task Migrate_WhenDone_GivesTheMaintenanceRoleNoTablePrivilegesAsync()
    {
        await using var services = CreateServices(bootstrap: true, modules => modules.AddModuleDbContext<IsolatedModuleContext>(IsolatedModuleContext.Schema));
        await services.GetRequiredService<DatabaseMigrator>().MigrateAsync(CancellationToken);

        (await QueryAsync("SELECT has_schema_privilege('backroom_maintenance', 'isolated_module', 'USAGE')::text")).Should().Equal("true");
        (await QueryAsync("SELECT has_schema_privilege('backroom_maintenance', 'isolated_module', 'CREATE')::text")).Should().Equal("false");
        (await QueryAsync(
            "SELECT p || ':' || has_table_privilege('backroom_maintenance', 'isolated_module.items', p)::text FROM unnest(ARRAY['SELECT', 'INSERT', 'UPDATE', 'DELETE', 'TRUNCATE']) AS p"))
            .Should().OnlyContain(row => row.EndsWith(":false", StringComparison.Ordinal));
        (await QueryAsync("SELECT has_database_privilege('backroom_maintenance', current_database(), 'CONNECT')::text")).Should().Equal("true");
    }

    [Fact]
    public async Task Migrate_WhenDone_GivesNoOneTheMigratorsFunctionsAsync()
    {
        await using var services = CreateServices(bootstrap: true, modules => modules.AddModuleDbContext<IsolatedModuleContext>(IsolatedModuleContext.Schema));
        await services.GetRequiredService<DatabaseMigrator>().MigrateAsync(CancellationToken);
        await ExecuteAsMigratorAsync("CREATE OR REPLACE FUNCTION isolated_module.answer() RETURNS int LANGUAGE sql AS 'SELECT 42'");

        (await QueryAsync("SELECT has_function_privilege('backroom_app', 'isolated_module.answer()', 'EXECUTE')::text")).Should().Equal("false");
    }

    [Fact]
    public async Task Migrate_WhenATableLacksRowLevelSecurity_FailsAsync()
    {
        await using var services = CreateServices(bootstrap: true, modules => modules.AddModuleDbContext<LeakyModuleContext>(LeakyModuleContext.Schema));

        var migrate = () => services.GetRequiredService<DatabaseMigrator>().MigrateAsync(CancellationToken);

        await migrate.Should().ThrowAsync<InvalidOperationException>().WithMessage("*leaky_module.items*row-level security*");
    }

    [Fact]
    public async Task Migrate_WhenNotOrganizationScoped_SkipsTheIsolationCheckAsync()
    {
        await using var services = CreateServices(
            bootstrap: true, modules => modules.AddModuleDbContext<LeakyModuleContext>(LeakyModuleContext.Schema, organizationScoped: false));

        await services.GetRequiredService<DatabaseMigrator>().MigrateAsync(CancellationToken);
    }

    [Fact]
    public async Task Migrate_WhenTwoModulesShareASchema_FailsAsync()
    {
        await using var services = CreateServices(bootstrap: true, modules =>
        {
            modules.AddModuleDbContext<IsolatedModuleContext>(IsolatedModuleContext.Schema);
            modules.AddModuleDbContext<LeakyModuleContext>(IsolatedModuleContext.Schema);
        });

        var migrate = () => services.GetRequiredService<DatabaseMigrator>().MigrateAsync(CancellationToken);

        await migrate.Should().ThrowAsync<InvalidOperationException>().WithMessage("*isolated_module*");
    }

    [Fact]
    public async Task Migrate_AsSuperuser_FailsAsync()
    {
        await using var services = CreateServices(bootstrap: false, _ => { }, migratorConnectionString: server.AdministratorConnectionString);

        var migrate = () => services.GetRequiredService<DatabaseMigrator>().MigrateAsync(CancellationToken);

        await migrate.Should().ThrowAsync<InvalidOperationException>().WithMessage("*migrator role*");
    }

    [Fact]
    public void Options_WithBootstrapOutsideDevelopment_FailValidation()
    {
        var services = new ServiceCollection();
        services.AddDatabaseMigrations(allowBootstrap: false).Configure(options =>
        {
            options.MigratorConnectionString = "Host=localhost;Username=backroom_migrator";
            options.BootstrapConnectionString = "Host=localhost;Username=postgres";
        });
        using var provider = services.BuildServiceProvider();

        var read = () => provider.GetRequiredService<IOptions<DatabaseMigrationOptions>>().Value;

        read.Should().Throw<OptionsValidationException>().WithMessage("*only in Development*");
    }

    private ServiceProvider CreateServices(bool bootstrap, Action<IServiceCollection> modules, string? migratorConnectionString = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        modules(services);
        services.AddDatabaseMigrations(allowBootstrap: true).Configure(options =>
        {
            options.MigratorConnectionString = migratorConnectionString ?? server.ConnectionStringFor(DatabaseRoles.Migrator, server.MigratorPassword);
            if (bootstrap)
            {
                options.BootstrapConnectionString = server.AdministratorConnectionString;
                options.MigratorPassword = server.MigratorPassword;
                options.ApplicationPassword = server.ApplicationPassword;
                options.RelayPassword = server.RelayPassword;
                options.MaintenancePassword = server.MaintenancePassword;
            }
        });
        return services.BuildServiceProvider();
    }

    private Task BootstrapAsync() =>
        DatabaseBootstrapper.BootstrapAsync(
            server.AdministratorConnectionString, server.MigratorPassword, server.ApplicationPassword, server.RelayPassword, server.MaintenancePassword, CancellationToken);

    private async Task ExecuteAsMigratorAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(server.ConnectionStringFor(DatabaseRoles.Migrator, server.MigratorPassword));
        await connection.OpenAsync(CancellationToken);
#pragma warning disable CA2100 // Test SQL written in this class.
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        await command.ExecuteNonQueryAsync(CancellationToken);
    }

    private async Task<List<string>> QueryAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(server.AdministratorConnectionString);
        await connection.OpenAsync(CancellationToken);
#pragma warning disable CA2100 // Test SQL written in this class.
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        await using var reader = await command.ExecuteReaderAsync(CancellationToken);
        var rows = new List<string>();
        while (await reader.ReadAsync(CancellationToken))
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }
}
