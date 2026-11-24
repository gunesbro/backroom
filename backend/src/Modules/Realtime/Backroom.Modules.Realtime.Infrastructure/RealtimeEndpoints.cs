using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Backroom.Modules.Realtime.Infrastructure.Gateway;

namespace Backroom.Modules.Realtime.Infrastructure;

public static class RealtimeEndpoints
{
    /// <summary>The hub's path (docs/contracts/phase-1.md, "Live events"). Only here may the access token come in the query string.</summary>
    public const string HubPath = "/hubs/realtime";

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

            // Clients send two ids at a time; the server sends a message at a time.
            connection.ApplicationMaxBufferSize = limits.MaxReceiveMessageSizeBytes;
            connection.TransportMaxBufferSize = 64 * 1024;
        }).RequireAuthorization();
    }
}
