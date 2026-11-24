using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Backroom.Modules.ControlPlaneConnector.Infrastructure.Link;

namespace Backroom.Modules.ControlPlaneConnector.Infrastructure.Epoch;

/// <summary>
/// The worker's writes of the cell's epoch (docs/adr/0011, "Cell epoch"), as the relay role: the app role may only
/// read it. Each method is one statement.
/// </summary>
internal sealed class CellEpochStore(
    [FromKeyedServices(UpstreamDelivery.DataSourceKey)] NpgsqlDataSource dataSource, TimeProvider timeProvider)
{
    private const string ConfirmSql = """
        INSERT INTO control_plane_connector.cell_epoch (cell_id, epoch, confirmed, updated_at)
        VALUES ($1, $2, true, $3)
        ON CONFLICT (cell_id) DO UPDATE SET epoch = EXCLUDED.epoch, confirmed = true, updated_at = EXCLUDED.updated_at
        """;

    private const string UnconfirmSql = """
        UPDATE control_plane_connector.cell_epoch SET confirmed = false, updated_at = $2 WHERE cell_id = $1 AND confirmed
        """;

    /// <summary>Stores the epoch <c>Welcome</c> carried and confirms it: the app role accepts writes again.</summary>
    public async Task ConfirmAsync(string cellId, long epoch, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(epoch, 1);
        await using var command = dataSource.CreateCommand(ConfirmSql);
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = cellId });
        command.Parameters.Add(new NpgsqlParameter<long> { TypedValue = epoch });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = timeProvider.GetUtcNow() });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>The epoch changed on the control plane: the app role refuses writes until the next <c>Welcome</c>.</summary>
    public async Task UnconfirmAsync(string cellId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(UnconfirmSql);
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = cellId });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = timeProvider.GetUtcNow() });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
