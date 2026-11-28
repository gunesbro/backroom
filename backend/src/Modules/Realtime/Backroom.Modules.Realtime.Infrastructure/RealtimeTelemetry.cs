using System.Diagnostics.Metrics;

namespace Backroom.Modules.Realtime.Infrastructure;

/// <summary>Metrics of the gateway. Tags carry event names only, never ids or content.</summary>
public static class RealtimeTelemetry
{
    public const string MeterName = "Backroom.Realtime";

    private static readonly Meter s_meter = new(MeterName);

    internal static readonly UpDownCounter<long> Connections =
        s_meter.CreateUpDownCounter<long>("backroom.realtime.connections", "{connection}", "Open hub connections on this node.");

    internal static readonly UpDownCounter<long> Subscriptions =
        s_meter.CreateUpDownCounter<long>("backroom.realtime.subscriptions", "{subscription}", "Group subscriptions of this node's connections.");

    internal static readonly Counter<long> Publishes =
        s_meter.CreateCounter<long>("backroom.realtime.publishes", "{event}", "Live events handed to Valkey.");

    internal static readonly Counter<long> PublishFailures =
        s_meter.CreateCounter<long>("backroom.realtime.publish_failures", "{event}", "Live events dropped (Valkey failed, timed out or the node was saturated).");

    internal static readonly Counter<long> DroppedTyping =
        s_meter.CreateCounter<long>("backroom.realtime.dropped_typing", "{call}", "Typing calls dropped by the throttle (at most one delivered per account and conversation per 3 s).");

    internal static readonly Counter<long> RevokedRequests =
        s_meter.CreateCounter<long>("backroom.realtime.revoked_requests", "{request}", "Requests and hub connects refused because the token's session was revoked.");

    internal static readonly Counter<long> RevocationChecksUnavailable =
        s_meter.CreateCounter<long>("backroom.realtime.revocation_checks_unavailable", "{request}", "Requests answered 503 because the revocation store could not be read.");

    internal static readonly Counter<long> RevokedConnections =
        s_meter.CreateCounter<long>("backroom.realtime.revoked_connections", "{connection}", "Hub connections closed because their session was revoked.");

    internal static readonly Counter<long> DroppedActivity =
        s_meter.CreateCounter<long>("backroom.realtime.dropped_activity", "{report}", "Activity reports dropped because a connection sent them more often than allowed.");

    internal static readonly Counter<long> RejectedSubscriptions =
        s_meter.CreateCounter<long>("backroom.realtime.rejected_subscriptions", "{request}", "Subscribe calls refused (reason tag).");
}
