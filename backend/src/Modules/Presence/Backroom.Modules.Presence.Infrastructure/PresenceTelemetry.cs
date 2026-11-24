using System.Diagnostics.Metrics;

namespace Backroom.Modules.Presence.Infrastructure;

/// <summary>Metrics of presence. Tags carry states and operation names only, never ids or status text.</summary>
public static class PresenceTelemetry
{
    public const string MeterName = "Backroom.Presence";

    private static readonly Meter s_meter = new(MeterName);

    internal static readonly Counter<long> Changes =
        s_meter.CreateCounter<long>("backroom.presence.changes", "{change}", "Presence changes published (state tag).");

    internal static readonly Counter<long> Failures =
        s_meter.CreateCounter<long>("backroom.presence.failures", "{operation}", "Presence operations that failed against Valkey (operation tag).");
}
