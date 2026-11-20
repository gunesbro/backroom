using Microsoft.Extensions.Logging;
using Backroom.BuildingBlocks.Domain.Errors;
using Backroom.Modules.CellManagement.Application.Registration;

namespace Backroom.Modules.CellManagement.Application.Provisioning;

/// <summary>Adds a pending cell and returns its one-time registration key, which is shown only once.</summary>
internal sealed partial class CellProvisioningService(
    ICellRegistry registry,
    SharedCellOptions sharedCells,
    TimeProvider timeProvider,
    ILogger<CellProvisioningService> logger)
{
    public async Task<Result<ProvisionedCell>> ProvisionAsync(NewCell cell, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cell);
        if (!cell.IsValid())
        {
            return CellManagementErrors.CellInvalid;
        }

        // Operators add cells rarely and one at a time, so two racing additions past the limit are not guarded.
        if (cell.Type == "shared" && await registry.CountSharedCellsAsync(cancellationToken) >= sharedCells.Max)
        {
            return CellManagementErrors.SharedCellLimit;
        }

        var key = RegistrationKeys.Create();
        var expiresAt = timeProvider.GetUtcNow() + RegistrationKeys.Lifetime;
        if (!await registry.AddCellAsync(cell, RegistrationKeys.Hash(key), expiresAt, cancellationToken))
        {
            return CellManagementErrors.CellExists;
        }

        LogProvisioned(logger, cell.Id);
        return new ProvisionedCell(cell.Id, key, expiresAt);
    }

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Security: cell {CellId} added with a registration key.")]
    private static partial void LogProvisioned(ILogger logger, string cellId);
}
