using Backroom.BuildingBlocks.Application.Cells;

namespace Backroom.Modules.Realtime.IntegrationTests;

/// <summary>A cell whose epoch is confirmed, for the hosts that run Messaging without the control plane connector.</summary>
internal sealed class FixedCellEpoch : ICellEpoch
{
    public ValueTask<long?> ConfirmedEpochAsync(CancellationToken cancellationToken) => ValueTask.FromResult<long?>(1);
}
