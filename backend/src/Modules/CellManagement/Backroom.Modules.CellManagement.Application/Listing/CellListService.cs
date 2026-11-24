using Backroom.Modules.CellManagement.Application.Registration;
using Backroom.Modules.CellManagement.Contracts;

namespace Backroom.Modules.CellManagement.Application.Listing;

/// <summary>
/// The operations panel's cell list. Link status comes from the last heartbeat, which every control-plane
/// instance can read; a cell is connected while a heartbeat is younger than
/// <see cref="ICellCatalog.HealthyHeartbeatAge"/>.
/// </summary>
internal sealed class CellListService(ICellRegistry registry, ICellOrganizationCounts organizationCounts, TimeProvider timeProvider)
{
    public const int MaxPageSize = 1_000;

    public const string Connected = "connected";

    public const string Disconnected = "disconnected";

    public async Task<CellPage> ListAsync(string? after, int limit, CancellationToken cancellationToken)
    {
        limit = Math.Clamp(limit, 1, MaxPageSize);

        // One more than asked tells whether another page follows.
        var rows = await registry.ListCellsAsync(after, limit + 1, cancellationToken);
        var hasMore = rows.Count > limit;
        var page = hasMore ? [.. rows.Take(limit)] : rows;
        var counts = await organizationCounts.CountByCellAsync([.. page.Select(row => row.Id)], cancellationToken);
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
}
