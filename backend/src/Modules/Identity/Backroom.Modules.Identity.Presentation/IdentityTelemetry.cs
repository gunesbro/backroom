using System.Diagnostics.Metrics;

namespace Backroom.Modules.Identity.Presentation;

/// <summary>Metrics of the back-channel logout endpoint. No tags that carry ids.</summary>
public static class IdentityTelemetry
{
    public const string MeterName = "Backroom.Identity";

    private static readonly Meter s_meter = new(MeterName);

    /// <summary>
    /// A valid logout token whose handling failed (database down): the revocation was lost, because Keycloak does not retry, and the
    /// session stays usable until its access token expires (at most five minutes). Alert on any.
    /// </summary>
    internal static readonly Counter<long> LogoutFailures =
        s_meter.CreateCounter<long>("backroom.identity.logout_failures", "{logout}", "Valid back-channel logouts that could not be recorded.");

    internal static readonly Counter<long> LogoutRefusals =
        s_meter.CreateCounter<long>("backroom.identity.logout_refusals", "{logout}", "Back-channel logout calls refused (invalid token).");
}
