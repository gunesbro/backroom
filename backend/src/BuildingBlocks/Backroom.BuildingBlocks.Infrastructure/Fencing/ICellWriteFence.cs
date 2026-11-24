namespace Backroom.BuildingBlocks.Infrastructure.Fencing;

/// <summary>
/// Whether the cell may change state now (docs/adr/0011, "Cell epoch"): after a restore or a failover it may not until
/// it has confirmed its current epoch with the control plane. The app role's write fence, the integration-event
/// consumers and the maintenance jobs ask it; the control plane connector implements it. Without a registration
/// (the control plane) nothing is fenced.
/// </summary>
public interface ICellWriteFence
{
    /// <summary><see langword="true"/> while state may change; <see langword="false"/> while writes are fenced.</summary>
    ValueTask<bool> AllowsWritesAsync(CancellationToken cancellationToken);
}
