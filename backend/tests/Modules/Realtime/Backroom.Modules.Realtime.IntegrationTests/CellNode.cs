using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.BuildingBlocks.Presentation.Authentication;
using Backroom.Modules.Messaging.Infrastructure;
using Backroom.Modules.Messaging.Presentation;
using Backroom.Modules.Organization.Infrastructure;
using Backroom.Modules.Permissions.Infrastructure;
using Backroom.Modules.Realtime.Infrastructure;
using Backroom.Testing.Postgres;

namespace Backroom.Modules.Realtime.IntegrationTests;

/// <summary>
/// One app node of a cell on a test server: the messaging endpoints and the realtime hub, with real tokens, PostgreSQL
/// and Valkey. Nodes of one cell share the cell id, the database and the Valkey server, as in a deployment.
/// </summary>
internal sealed class CellNode : IAsyncDisposable
{
    private readonly WebApplication _app;

    private CellNode(WebApplication app, CapturingLoggerProvider logs)
    {
        _app = app;
        Logs = logs;
        Client = app.GetTestClient();
        Server = app.GetTestServer();
    }

    public CapturingLoggerProvider Logs { get; }

    public HttpClient Client { get; }

    public TestServer Server { get; }

    public IServiceProvider Services => _app.Services;

    public static async Task<CellNode> StartAsync(
        PostgresDatabase database,
        string authority,
        string cellId,
        string valkeyConnectionString,
        IReadOnlyDictionary<string, string?>? settings = null,
        SecurityKey? extraSigningKey = null,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        var logs = new CapturingLoggerProvider();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        builder.Logging.AddProvider(logs);
        builder.Configuration["Authentication:Authority"] = authority;
        builder.Configuration["Database:ConnectionString"] = database.ApplicationConnectionString();
        builder.Configuration["Cell:Id"] = cellId;
        builder.Configuration["Valkey:ConnectionString"] = valkeyConnectionString;
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            builder.Configuration[key] = value;
        }

        // The same call as the cell host's.
        builder.AddAccessTokenAuthentication(
            Audiences.Cell(cellId), [KeycloakClients.Web, KeycloakClients.Desktop], RealtimeEndpoints.HubPath);
        if (extraSigningKey is not null)
        {
            // A second trusted key, for tokens a test signs itself (the real keys come from Keycloak).
            builder.Services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, jwt =>
                jwt.TokenValidationParameters.IssuerSigningKeys = [extraSigningKey]);
        }

        if (timeProvider is not null)
        {
            builder.Services.AddSingleton(timeProvider);
        }

        builder.Services.AddDatabaseSession().BindConfiguration(DatabaseOptions.SectionName);
        builder.Services.AddPermissions();
        builder.Services.AddOrganization();
        builder.Services.AddRealtime();
        builder.Services.AddRealtimeGateway();
        builder.Services.AddMessaging();
        builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);

        var app = builder.Build();
        app.UseAuthentication();
        app.UseRevokedSessionCheck();
        app.UseAuthorization();
        app.MapMessagingEndpoints();
        app.MapRealtimeHub();
        await app.StartAsync(cancellationToken);
        return new CellNode(app, logs);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.DisposeAsync();
    }
}
