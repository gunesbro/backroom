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

    internal static readonly Counter<long> RejectedSubscriptions =
        s_meter.CreateCounter<long>("backroom.realtime.rejected_subscriptions", "{request}", "Subscribe calls refused (reason tag).");
}
