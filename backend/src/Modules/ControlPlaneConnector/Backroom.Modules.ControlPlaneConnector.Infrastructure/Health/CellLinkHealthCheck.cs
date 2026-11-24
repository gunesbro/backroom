using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Backroom.Modules.ControlPlaneConnector.Infrastructure.Health;

/// <summary>Unhealthy while the cell's registration or credentials are rejected (<see cref="CellLinkStatus"/>).</summary>
internal sealed class CellLinkHealthCheck(CellLinkStatus status) : IHealthCheck
{
    public const string Name = "control-plane-link";

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(status.Rejection is { } reason ? HealthCheckResult.Unhealthy(reason) : HealthCheckResult.Healthy());
}
