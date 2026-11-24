using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.BuildingBlocks.Presentation.Authentication;
using Backroom.BuildingBlocks.Presentation.Cors;
using Backroom.Modules.ControlPlaneConnector.Infrastructure.Epoch;
using Backroom.Modules.Files.Presentation;
using Backroom.Modules.Notifications.Presentation;
using Backroom.Modules.Messaging.Presentation;
using Backroom.Modules.Organization.Presentation;
using Backroom.Modules.Presence.Presentation;
using Backroom.Modules.Realtime.Infrastructure;
using Backroom.ServiceDefaults;

namespace Backroom.Cell.Host;

/// <summary>
/// The cell host's services and request pipeline for the app and worker roles (the migrate role runs
/// <see cref="CellModules.MigrateAsync"/> instead). Program runs it; tests build it on a test server to check the
/// pipeline's order.
/// </summary>
internal static class CellApp
{
    public static WebApplication Build(WebApplicationBuilder builder, CellRole role)
    {
        ArgumentNullException.ThrowIfNull(builder);
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
        if (role == CellRole.App)
        {
            // Writes wait for the cell's confirmed epoch (docs/adr/0011, "Cell epoch"); the worker confirms it over the link.
            builder.Services.AddCellEpochFence();
        }

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
        if (role == CellRole.App)
        {
            // After authorization (a caller without a token still gets 401), before rate limiting. The hub is exempt: its
            // negotiate is a POST, and its methods only subscribe.
            app.UseCellEpochFence(RealtimeEndpoints.HubPath);
        }

        app.UseRateLimiter();
        app.MapDefaultEndpoints();
        if (role == CellRole.App)
        {
            app.MapOrganizationEndpoints();
            app.MapMessagingEndpoints();
            app.MapPresenceEndpoints();
            if (app.Configuration.GetValue<bool>(FilesEndpoints.FilesFlag))
            {
                app.MapFilesEndpoints();
            }

            if (app.Configuration.GetValue<bool>(NotificationsEndpoints.Flag))
            {
                app.MapNotificationsEndpoints();
            }

            app.MapRealtimeHub();
        }

        return app;
    }
}
