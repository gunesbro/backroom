namespace Backroom.Modules.ControlPlaneConnector.Infrastructure.Health;

/// <summary>
/// Whether the control plane or Keycloak rejected the cell's registration or credentials, which retrying does not fix:
/// for example a local registration marker the control plane does not know (a reset control plane, or a key directory
/// copied from another environment). The workers keep retrying with backoff, so an operator's repair takes effect
/// without a restart, but the state is reported as unhealthy instead of a silent loop. The cell never re-registers on
/// its own: registration keys are single-use and bound to the first key that claims them.
/// </summary>
internal sealed class CellLinkStatus
{
    private volatile string? _rejection;

    /// <summary>Why the cell is rejected (a fixed text without ids or secrets), or null.</summary>
    public string? Rejection => _rejection;

    public void Reject(string reason) => _rejection = reason;

    public void Clear() => _rejection = null;
}
