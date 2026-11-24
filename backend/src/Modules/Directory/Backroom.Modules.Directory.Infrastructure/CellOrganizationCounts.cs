using Npgsql;
using Backroom.Modules.CellManagement.Contracts;

namespace Backroom.Modules.Directory.Infrastructure;

/// <summary>Organizations per cell for the operations panel: one grouped query on the cell index; failed organizations are not counted.</summary>
internal sealed class CellOrganizationCounts(NpgsqlDataSource dataSource) : ICellOrganizationCounts
{
    private const string Sql = """
        SELECT cell_id, count(*)::int FROM directory.organizations
        WHERE cell_id = ANY($1) AND status <> 'failed'
        GROUP BY cell_id
        """;

    public async Task<IReadOnlyDictionary<string, int>> CountByCellAsync(IReadOnlyCollection<string> cellIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cellIds);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(cellIds.Count, ICellOrganizationCounts.MaxCellsPerLookup, nameof(cellIds));
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        if (cellIds.Count == 0)
        {
            return counts;
        }

        await using var command = dataSource.CreateCommand(Sql);
        command.Parameters.Add(new NpgsqlParameter<string[]> { TypedValue = [.. cellIds] });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            counts[reader.GetString(0)] = reader.GetInt32(1);
        }

        return counts;
    }
}
