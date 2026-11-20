using Npgsql;
using Backroom.Modules.CellManagement.Contracts;

namespace Backroom.Modules.CellManagement.Infrastructure;

/// <summary>Cell addresses from the registry, one query per lookup (primary key).</summary>
internal sealed class CellCatalog(NpgsqlDataSource dataSource, TimeProvider timeProvider) : ICellCatalog
{
    private const string Sql = """
        SELECT id, public_url FROM cell_management.cells
        WHERE id = ANY($1) AND status = 'registered'
        """;

    private const string HealthySql = """
        SELECT id, public_url FROM cell_management.cells
        WHERE region = $1 AND type = 'shared' AND status = 'registered' AND last_heartbeat_at > $2
        ORDER BY id
        LIMIT $3
        """;

    public async Task<IReadOnlyList<CellAddress>> GetHealthyCellsAsync(string region, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(HealthySql);
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = region });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = timeProvider.GetUtcNow() - ICellCatalog.HealthyHeartbeatAge });
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = ICellCatalog.MaxCellsPerLookup });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var cells = new List<CellAddress>();
        while (await reader.ReadAsync(cancellationToken))
        {
            cells.Add(new CellAddress(reader.GetString(0), new Uri(reader.GetString(1), UriKind.Absolute)));
        }

        return cells;
    }

    public async Task<IReadOnlyDictionary<string, CellAddress>> GetRegisteredCellsAsync(
        IReadOnlyCollection<string> cellIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cellIds);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(cellIds.Count, ICellCatalog.MaxCellsPerLookup, nameof(cellIds));
        var cells = new Dictionary<string, CellAddress>(StringComparer.Ordinal);
        if (cellIds.Count == 0)
        {
            return cells;
        }

        await using var command = dataSource.CreateCommand(Sql);
        command.Parameters.Add(new NpgsqlParameter<string[]> { TypedValue = [.. cellIds] });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetString(0);
            cells[id] = new CellAddress(id, new Uri(reader.GetString(1), UriKind.Absolute));
        }

        return cells;
    }
}
