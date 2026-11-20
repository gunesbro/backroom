using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.BuildingBlocks.Presentation.Authentication;
using Backroom.Modules.CellManagement.Infrastructure;
using Backroom.Modules.Directory.Infrastructure;
using Backroom.Modules.Directory.Infrastructure.Invitations;
using Backroom.Modules.Directory.Presentation;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;

namespace Backroom.Modules.Directory.IntegrationTests;

/// <summary>A control plane with Directory and CellManagement, as its host wires them, on a test server.</summary>
internal static class DirectoryUnderTest
{
    private static readonly SemaphoreSlim s_migrationLock = new(1, 1);
    private static bool s_migrated;

    public static async Task<WebApplication> StartAsync(
        PostgresDatabase database,
        KeycloakRealm keycloak,
        CancellationToken cancellationToken,
        TimeProvider? timeProvider = null,
        string? emailHmacKey = null,
        bool invitations = true,
        Action<IServiceCollection>? configureServices = null)
    {
        await MigrateAsync(database, cancellationToken);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.WebHost.UseTestServer();
        builder.Configuration["Authentication:Authority"] = keycloak.Url;
        builder.Configuration["Database:ConnectionString"] = database.ApplicationConnectionString();
        builder.Configuration["CellManagement:Keycloak:AdminClientSecret"] = keycloak.ControlPlaneAdminSecret;
        builder.Configuration[DirectoryEndpoints.OrganizationCreationFlag] = "true";
        builder.Configuration[DirectoryEndpoints.InvitationsFlag] = invitations ? "true" : "false";
        if (timeProvider is not null)
        {
            builder.Services.AddSingleton(timeProvider);
        }

        builder.AddAccessTokenAuthentication(Audiences.ControlPlane, [KeycloakClients.Web, KeycloakClients.Desktop, KeycloakClients.Operations]);
        builder.Services.AddDatabaseSession().BindConfiguration(DatabaseOptions.SectionName);
        // Test cells pile up in the shared test database; the contract limit is checked by SharedCellsTests.
        builder.Configuration["CellManagement:SharedCells:Max"] = "100000";
        builder.AddCellManagement();
        builder.Configuration[$"{InvitationEmailHmacOptions.SectionName}:EmailHmacKey"] = emailHmacKey ?? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        builder.Services.AddDirectory().BindConfiguration(InvitationEmailHmacOptions.SectionName);
        configureServices?.Invoke(builder.Services);
        builder.Services.AddDirectoryEndpoints();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapDirectoryEndpoints();
        await app.StartAsync(cancellationToken);
        return app;
    }

    private static async Task MigrateAsync(PostgresDatabase database, CancellationToken cancellationToken)
    {
        await s_migrationLock.WaitAsync(cancellationToken);
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
            services.AddDatabaseMigrations(allowBootstrap: false).Configure(options => options.MigratorConnectionString = database.MigratorConnectionString);
            await using var provider = services.BuildServiceProvider();
            await provider.GetRequiredService<DatabaseMigrator>().MigrateAsync(cancellationToken);
            s_migrated = true;
        }
        finally
        {
            s_migrationLock.Release();
        }
    }
}
