using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.BuildingBlocks.Presentation.Authentication;
using Backroom.BuildingBlocks.Presentation.Cors;
using Backroom.Cell.Host;
using Backroom.Modules.Messaging.Presentation;
using Backroom.Modules.Organization.Presentation;
using Backroom.Modules.Presence.Presentation;
using Backroom.Modules.Realtime.Infrastructure;
using Backroom.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

var role = CellRoleParser.Parse(builder.Configuration[CellRoleParser.ConfigurationKey]);
if (role == CellRole.Migrate)
{
    await CellModules.MigrateAsync(args);
    return;
}

var cellId = builder.Configuration[CellIdentity.ConfigurationKey]
    ?? throw new InvalidOperationException($"{CellIdentity.ConfigurationKey} is required.");

builder.AddServiceDefaults();
builder.AddWebOriginsCors();

// This cell accepts only tokens issued for it (audience cell-{id}).
// The SignalR hub's WebSocket cannot carry a header, so its path (only) accepts the token as access_token.
builder.AddAccessTokenAuthentication(Audiences.Cell(cellId), [KeycloakClients.Web, KeycloakClients.Desktop], RealtimeEndpoints.HubPath);

// The application role's connection pool and the unit of work.
builder.Services.AddDatabaseSession().BindConfiguration(DatabaseOptions.SectionName);
builder.AddCellModules(role);
// UseRateLimiter needs its services in every role; only the app role maps endpoints that use them.
builder.Services.AddOrganizationEndpoints();
builder.Services.AddPresenceEndpoints();

// Role-specific services (endpoints for "app", consumers and jobs for "worker") are registered by the modules.

var app = builder.Build();

app.UseDefaultErrorHandling();
app.UseWebOriginsCors();

// Before authentication, which takes the hub's query token only for a WebSocket upgrade.
app.UseRealtimeWebSockets();
app.UseAuthentication();
if (role == CellRole.App)
{
    // Tokens of revoked sessions are refused on every request and hub connect (docs/contracts/phase-1.md, "Identity and tokens").
    app.UseRevokedSessionCheck();
}

app.UseAuthorization();
app.UseRateLimiter();
app.MapDefaultEndpoints();
if (role == CellRole.App)
{
    app.MapOrganizationEndpoints();
    app.MapMessagingEndpoints();
    app.MapPresenceEndpoints();
    app.MapRealtimeHub();
}

CellHostLog.Starting(app.Logger, role);

await app.RunAsync();
