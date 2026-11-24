using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Backroom.Modules.Realtime.Infrastructure.Gateway;
using Backroom.Modules.Realtime.Infrastructure.Revocation;

namespace Backroom.Modules.Realtime.Infrastructure;

public static class RealtimeEndpoints
{
    /// <summary>The hub's path (docs/contracts/phase-1.md, "Live events"). Only here may the access token come in the query string.</summary>
    public const string HubPath = "/hubs/realtime";

    /// <summary>
    /// Before <c>UseAuthentication</c>: marks WebSocket upgrades, so authentication can tell the hub's WebSocket request (the
    /// only one that may carry the token in the query string) from the others. The hub's own pipeline adds the same
    /// middleware with the same default options, but only after authentication has run; on Kestrel, without this,
    /// authentication never sees an upgrade and a browser's connect is answered 401.
    /// </summary>
    public static IApplicationBuilder UseRealtimeWebSockets(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseWebSockets();
    }

    /// <summary>
    /// After <c>UseAuthentication</c>: refuses a token whose session was revoked (<c>401</c>) and, when Valkey cannot be read, any token
    /// not recently checked (<c>503</c>). Covers every request and hub connect of the app role.
    /// </summary>
    public static IApplicationBuilder UseRevokedSessionCheck(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<RevocationMiddleware>();
    }

    /// <summary>
    /// Maps the hub (app role). It requires an authenticated person like every endpoint; the connection ends when its
    /// token expires, and the client reconnects with a fresh one.
    /// </summary>
    public static IEndpointConventionBuilder MapRealtimeHub(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var limits = endpoints.ServiceProvider.GetRequiredService<IOptions<RealtimeOptions>>().Value;
        return endpoints.MapHub<RealtimeHub>(HubPath, connection =>
        {
            connection.CloseOnAuthenticationExpiration = true;

            // WebSockets only: the one transport that carries the token in the query string without the long-lived requests of
            // the others, and the only one the clients need (a client without it cannot connect, and says so).
            connection.Transports = HttpTransportType.WebSockets;

            // Clients send two ids at a time; the server sends a message at a time.
            connection.ApplicationMaxBufferSize = limits.MaxReceiveMessageSizeBytes;
            connection.TransportMaxBufferSize = 64 * 1024;
        }).RequireAuthorization();
    }
}
