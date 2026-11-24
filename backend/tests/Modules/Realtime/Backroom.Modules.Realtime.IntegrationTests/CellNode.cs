using System.Net;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
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
using Backroom.Modules.Presence.Infrastructure;
using Backroom.Modules.Presence.Presentation;
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
    private readonly TestServer? _server;

    private CellNode(WebApplication app, CapturingLoggerProvider logs, bool kestrel)
    {
        _app = app;
        Logs = logs;
        if (kestrel)
        {
            BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.GetRequiredFeature<IServerAddressesFeature>().Addresses.Single());
            Client = new HttpClient { BaseAddress = BaseAddress };
        }
        else
        {
            Client = app.GetTestClient();
            _server = app.GetTestServer();
            BaseAddress = _server.BaseAddress;
        }
    }

    public CapturingLoggerProvider Logs { get; }

    public HttpClient Client { get; }

    /// <summary>The test server; a Kestrel node has none (use <see cref="BaseAddress"/>).</summary>
    public TestServer Server => _server
        ?? throw new InvalidOperationException("This node runs on Kestrel and has no test server; connect to BaseAddress instead.");

    public Uri BaseAddress { get; }

    public IServiceProvider Services => _app.Services;

    public static async Task<CellNode> StartAsync(
        PostgresDatabase database,
        string authority,
        string cellId,
        string valkeyConnectionString,
        IReadOnlyDictionary<string, string?>? settings = null,
        SecurityKey? extraSigningKey = null,
        TimeProvider? timeProvider = null,
        bool kestrel = false,
        CancellationToken cancellationToken = default)
    {
        var logs = new CapturingLoggerProvider();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        if (kestrel)
        {
            // Real Kestrel on a free loopback port: unlike the test server's WebSocket client, it does not set the
            // WebSocket feature itself, so only the pipeline's own middleware can make authentication see the upgrade.
            builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        }
        else
        {
            builder.WebHost.UseTestServer();
        }

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
        builder.Services.AddSingleton<Backroom.BuildingBlocks.Application.Cells.ICellEpoch, FixedCellEpoch>();
        builder.Services.AddMessaging();
        builder.Services.AddPresence();
        builder.Services.AddPresenceTracking();
        builder.Services.AddPresenceEndpoints();
        builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);

        var app = builder.Build();
        app.UseRealtimeWebSockets();
        app.UseAuthentication();
        app.UseRevokedSessionCheck();
        app.UseAuthorization();
        app.UseRateLimiter();
        app.MapMessagingEndpoints();
        app.MapPresenceEndpoints();
        app.MapRealtimeHub();
        await app.StartAsync(cancellationToken);
        return new CellNode(app, logs, kestrel);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.DisposeAsync();
    }
}
