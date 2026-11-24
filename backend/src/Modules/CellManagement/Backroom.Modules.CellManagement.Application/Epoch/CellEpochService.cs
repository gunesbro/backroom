using Microsoft.Extensions.Logging;
using Backroom.BuildingBlocks.Domain.Errors;
using Backroom.Modules.CellManagement.Application.Link;
using Backroom.Modules.CellManagement.Application.Registration;

namespace Backroom.Modules.CellManagement.Application.Epoch;

/// <summary>
/// Increments a cell's epoch (docs/adr/0011, "Cell epoch"), the first step of the restore and failover procedure
/// (docs/runbooks/cell-restore-and-failover.md). The cell's open stream in this instance is ended at once, so the
/// cell fences its writes and reconnects to learn the new epoch; streams on other instances end at their next
/// heartbeat write.
/// </summary>
internal sealed partial class CellEpochService(ICellRegistry registry, CellLinkSessions sessions, ILogger<CellEpochService> logger)
{
    public async Task<Result<long>> IncrementAsync(string cellId, CancellationToken cancellationToken)
    {
        if (!CellIds.IsValid(cellId))
        {
            return CellManagementErrors.CellInvalid;
        }

        if (await registry.IncrementEpochAsync(cellId, cancellationToken) is not { } epoch)
        {
            return CellManagementErrors.CellNotFound;
        }

        sessions.EndForEpochChange(cellId);
        LogIncremented(logger, cellId, epoch);
        return epoch;
    }

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "Security: the epoch of cell {CellId} was incremented to {Epoch}.")]
    private static partial void LogIncremented(ILogger logger, string cellId, long epoch);
}
