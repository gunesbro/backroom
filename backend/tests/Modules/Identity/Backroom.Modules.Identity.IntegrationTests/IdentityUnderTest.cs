using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.BuildingBlocks.Presentation.Authentication;
using Backroom.Modules.CellManagement.Infrastructure;
using Backroom.Modules.Directory.Infrastructure;
using Backroom.Modules.Directory.Infrastructure.Invitations;
using Backroom.Modules.Identity.Infrastructure;
using Backroom.Modules.Identity.Presentation;
using Backroom.Testing.Postgres;

namespace Backroom.Modules.Identity.IntegrationTests;

/// <summary>A control plane with Identity, Directory and CellManagement as its host wires them, on a test server that trusts a test key as the realm's.</summary>
internal static class IdentityUnderTest
{
    private static readonly SemaphoreSlim s_migrationLock = new(1, 1);
    private static bool s_migrated;

    /// <param name="trustedKey">The key the host trusts as Keycloak's; when <see langword="null"/> the authority is the real one in <paramref name="authority"/>.</param>
    public static async Task<WebApplication> StartAsync(
        PostgresDatabase database,
        CancellationToken cancellationToken,
        TimeProvider timeProvider,
        Microsoft.IdentityModel.Tokens.SecurityKey? trustedKey = null,
        string authority = LogoutTokenIssuer.Issuer,
        string? listenOn = null)
    {
        await MigrateAsync(database, cancellationToken);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        if (listenOn is null)
        {
            builder.WebHost.UseTestServer();
        }
        else
        {
            builder.WebHost.UseUrls(listenOn);
        }

        builder.Configuration["Authentication:Authority"] = authority;
        builder.Configuration["Database:ConnectionString"] = database.ApplicationConnectionString();
        builder.Configuration["CellManagement:Keycloak:AdminClientSecret"] = "test-secret";
        builder.Services.AddSingleton(timeProvider);

        builder.AddAccessTokenAuthentication(Audiences.ControlPlane, [KeycloakClients.Web, KeycloakClients.Desktop, KeycloakClients.Operations]);
        if (trustedKey is not null)
        {
            builder.Services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                options.Configuration = new OpenIdConnectConfiguration { Issuer = authority, SigningKeys = { trustedKey } });
        }

        builder.Services.AddDatabaseSession().BindConfiguration(DatabaseOptions.SectionName);
        builder.Configuration["CellManagement:SharedCells:Max"] = "100000";
        builder.AddCellManagement();
        builder.Configuration[$"{InvitationEmailHmacOptions.SectionName}:EmailHmacKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        builder.Services.AddDirectory().BindConfiguration(InvitationEmailHmacOptions.SectionName);
        builder.Services.AddIdentity();
        builder.Services.AddIdentityEndpoints();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        app.MapIdentityEndpoints();
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
            services.AddIdentityDatabase();
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
