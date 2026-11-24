using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.BuildingBlocks.Presentation.Authentication;
using Backroom.Modules.CellManagement.Infrastructure;
using Backroom.Modules.CellManagement.Presentation;
using Backroom.Modules.ControlPlaneConnector.Infrastructure;
using Backroom.Modules.Directory.Contracts;
using Backroom.Modules.Directory.Infrastructure;
using Backroom.Modules.Identity.Infrastructure;
using Backroom.Modules.Organization.Infrastructure;
using Backroom.Modules.Permissions.Infrastructure;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;

namespace Backroom.Modules.CellManagement.IntegrationTests;

/// <summary>A control plane with the CellManagement module, as its host wires it, on a test server.</summary>
public static class ControlPlaneUnderTest
{
    private static readonly SemaphoreSlim s_migrationLock = new(1, 1);
    private static bool s_migrated;

    public static async Task<WebApplication> StartAsync(
        PostgresDatabase database, KeycloakRealm keycloak, TimeProvider? timeProvider = null, Action<IServiceCollection>? configureServices = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(keycloak);
        await MigrateAsync(database);

        // Development: the test Keycloak serves plain HTTP.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.WebHost.UseTestServer();
        builder.Configuration["Authentication:Authority"] = keycloak.Url;
        builder.Configuration["Database:ConnectionString"] = database.ApplicationConnectionString();
        builder.Configuration["CellManagement:Keycloak:AdminClientSecret"] = keycloak.ControlPlaneAdminSecret;
        if (timeProvider is not null)
        {
            builder.Services.AddSingleton(timeProvider);
        }

        builder.AddAccessTokenAuthentication(Audiences.ControlPlane, [KeycloakClients.Web, KeycloakClients.Desktop, KeycloakClients.Operations]);
        builder.AddCellTokenAuthentication();
        builder.Services.AddDatabaseSession().BindConfiguration(DatabaseOptions.SectionName);
        // Test cells pile up in the shared test database; the contract limit is checked by SharedCellsTests.
        builder.Configuration["CellManagement:SharedCells:Max"] = "100000";
        builder.AddCellManagement();
        builder.Services.AddCellManagementEndpoints();

        // What FullSync reads: Directory's queries and Identity's revoked sessions (not the rest of Directory, whose handlers
        // would change what these link tests observe).
        builder.Services.AddSingleton<IDirectoryQueries, DirectoryQueries>();
        builder.Services.AddIdentity();

        configureServices?.Invoke(builder.Services);
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        app.MapCellManagementEndpoints();
        await app.StartAsync();
        return app;
    }

    private static async Task MigrateAsync(PostgresDatabase database)
    {
        await s_migrationLock.WaitAsync();
        try
        {
            if (s_migrated)
            {
                return;
            }

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddCellManagementDatabase();
            services.AddDirectoryDatabase();
            services.AddIdentityDatabase();

            // The test cells share this database for the connector's link tables.
            services.AddControlPlaneConnectorDatabase();
            services.AddOrganizationDatabase();
            services.AddPermissionsDatabase();
            services.AddDatabaseMigrations(allowBootstrap: false).Configure(options => options.MigratorConnectionString = database.MigratorConnectionString);
            await using var provider = services.BuildServiceProvider();
            await provider.GetRequiredService<DatabaseMigrator>().MigrateAsync(CancellationToken.None);
            s_migrated = true;
        }
        finally
        {
            s_migrationLock.Release();
        }
    }
}
