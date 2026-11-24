using Npgsql;

namespace Backroom.Modules.ControlPlaneConnector.Infrastructure.Epoch;

/// <summary>
/// Whether the app role may accept writes (docs/adr/0011, "Cell epoch"): only while the cell's stored epoch is
/// confirmed. The answer comes from the cell's database (<c>control_plane_connector.cell_epoch</c>, written by the
/// worker) and is kept for <see cref="CacheLifetime"/>, so a node runs at most one indexed lookup per interval
/// however many writes it serves; one lookup at a time.
/// </summary>
internal sealed class CellEpochGate(NpgsqlDataSource dataSource, string cellId, TimeProvider timeProvider) : IDisposable
{
    /// <summary>How long an answer is kept; a confirmation cleared by the worker takes effect within this time.</summary>
    internal static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(2);

    private const string ConfirmedSql = "SELECT epoch FROM control_plane_connector.cell_epoch WHERE cell_id = $1 AND confirmed";

    private readonly SemaphoreSlim _lookup = new(1, 1);
    private volatile Answer? _answer;

    /// <summary>The confirmed epoch, or <see langword="null"/> while writes are fenced.</summary>
    public async ValueTask<long?> ConfirmedEpochAsync(CancellationToken cancellationToken)
    {
        if (_answer is { } cached && cached.ExpiresAt > timeProvider.GetUtcNow())
        {
            return cached.Epoch;
        }

        await _lookup.WaitAsync(cancellationToken);
        try
        {
            // Another request may have looked it up while this one waited.
            if (_answer is { } fresh && fresh.ExpiresAt > timeProvider.GetUtcNow())
            {
                return fresh.Epoch;
            }

            await using var command = dataSource.CreateCommand(ConfirmedSql);
            command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = cellId });
            var epoch = await command.ExecuteScalarAsync(cancellationToken) as long?;
            _answer = new Answer(epoch, timeProvider.GetUtcNow() + CacheLifetime);
            return epoch;
        }
        finally
        {
            _lookup.Release();
        }
    }

    public void Dispose() => _lookup.Dispose();

    private sealed record Answer(long? Epoch, DateTimeOffset ExpiresAt);
}
