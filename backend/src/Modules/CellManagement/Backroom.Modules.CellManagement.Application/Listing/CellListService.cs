using Microsoft.Extensions.Logging;
using Backroom.Modules.CellManagement.Application.Registration;
using Backroom.Modules.CellManagement.Contracts;

namespace Backroom.Modules.CellManagement.Application.Listing;

/// <summary>
/// The operations panel's cell list. Every call is an audit event (account id, action and result only; a
/// real audit log comes later). Link status comes from the last heartbeat, which every control-plane
/// instance can read; a cell is connected while a heartbeat is younger than
/// <see cref="ICellCatalog.HealthyHeartbeatAge"/>.
/// </summary>
internal sealed partial class CellListService(
    ICellRegistry registry,
    TimeProvider timeProvider,
    ILogger<CellListService> logger,
    ICellOrganizationCounts? organizationCounts = null)
{
    public const int MaxPageSize = 1_000;

    public const string Connected = "connected";

    public const string Disconnected = "disconnected";

    public async Task<CellPage> ListAsync(Guid accountId, string? after, int limit, CancellationToken cancellationToken)
    {
        try
        {
            var page = await ReadAsync(after, Math.Clamp(limit, 1, MaxPageSize), cancellationToken);
            LogAudit(logger, accountId, "ops.cells.list", "success", page.Cells.Count);
            return page;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogAuditFailed(logger, accountId, "ops.cells.list", "failure", exception.GetType().Name);
            throw;
        }
    }

    private async Task<CellPage> ReadAsync(string? after, int limit, CancellationToken cancellationToken)
    {
        // One more than asked tells whether another page follows.
        var rows = await registry.ListCellsAsync(after, limit + 1, cancellationToken);
        var hasMore = rows.Count > limit;
        var page = hasMore ? [.. rows.Take(limit)] : rows;
        var counts = await (organizationCounts ?? throw new InvalidOperationException("The directory module provides no organization counts.")).CountByCellAsync([.. page.Select(row => row.Id)], cancellationToken);
        var now = timeProvider.GetUtcNow();
        var cells = page.Select(row => new CellSummary(
            row.Id,
            row.Type,
            row.Region,
            row.Status,
            row.Version,
            row.LastHeartbeatAt is { } heartbeat && now - heartbeat <= ICellCatalog.HealthyHeartbeatAge ? Connected : Disconnected,
            row.LastHeartbeatAt,
            counts.GetValueOrDefault(row.Id),
            row.PendingMessages,
            row.RejectedMessages)).ToList();
        return new CellPage(cells, hasMore ? page[^1].Id : null);
    }

    [LoggerMessage(EventId = 20, Level = LogLevel.Information, Message = "Audit: account {AccountId} {Action} {Result} ({Count} cells).")]
    private static partial void LogAudit(ILogger logger, Guid accountId, string action, string result, int count);

    [LoggerMessage(EventId = 21, Level = LogLevel.Warning, Message = "Audit: account {AccountId} {Action} {Result} ({ExceptionType}).")]
    private static partial void LogAuditFailed(ILogger logger, Guid accountId, string action, string result, string exceptionType);
}
