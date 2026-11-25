namespace Backroom.BuildingBlocks.Application.Cells;

/// <summary>
/// The cell's confirmed epoch (docs/adr/0011, "Cell epoch"): the value every cursor and every live payload with a
/// sequence number carries, so a client can tell that the cell's database was restored or failed over. The control
/// plane connector implements it from the same cached lookup as the write fence.
/// </summary>
public interface ICellEpoch
{
    /// <summary>The confirmed epoch, or <see langword="null"/> while the cell has not confirmed one (writes are fenced then).</summary>
    ValueTask<long?> ConfirmedEpochAsync(CancellationToken cancellationToken);
}
