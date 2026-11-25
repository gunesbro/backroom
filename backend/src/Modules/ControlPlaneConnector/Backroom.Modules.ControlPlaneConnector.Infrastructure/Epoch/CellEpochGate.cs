using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Npgsql;
using Backroom.BuildingBlocks.Application.Cells;
using Backroom.BuildingBlocks.Infrastructure.Fencing;

namespace Backroom.Modules.ControlPlaneConnector.Infrastructure.Epoch;

/// <summary>
/// Whether the cell may change state (docs/adr/0011, "Cell epoch"): only while its stored epoch is confirmed. The answer
/// comes from the cell's database (<c>control_plane_connector.cell_epoch</c>, written by the worker) and is kept for
/// <see cref="CacheLifetime"/>, so a node runs at most one indexed lookup per interval however many writes it serves;
/// one lookup at a time. A failed lookup fences (fail closed) and is not kept. Used by the app role's write fence and,
/// in the worker, by the integration-event consumers and the maintenance jobs.
/// </summary>
internal sealed partial class CellEpochGate(NpgsqlDataSource dataSource, string cellId, TimeProvider timeProvider, ILogger<CellEpochGate> logger)
    : ICellWriteFence, ICellEpoch, IDisposable
{
    /// <summary>How long an answer is kept; a confirmation cleared by the worker takes effect within this time.</summary>
    internal static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(2);

    private const string ConfirmedSql = "SELECT epoch FROM control_plane_connector.cell_epoch WHERE cell_id = $1 AND confirmed";

    private readonly SemaphoreSlim _lookup = new(1, 1);
    private volatile Answer? _answer;

    public async ValueTask<bool> AllowsWritesAsync(CancellationToken cancellationToken) =>
        await ConfirmedEpochAsync(cancellationToken) is not null;

    /// <summary>The confirmed epoch, or <see langword="null"/> while writes are fenced.</summary>
    [SuppressMessage("Design", "CA1031", Justification = "Fail closed: a cell that cannot read its epoch must not change state.")]
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
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogLookupFailed(logger, exception.GetType().Name);
            return null;
        }
        finally
        {
            _lookup.Release();
        }
    }

    public void Dispose() => _lookup.Dispose();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reading the cell's epoch failed with {ExceptionType}; writes are fenced until it can be read.")]
    private static partial void LogLookupFailed(ILogger logger, string exceptionType);

    private sealed record Answer(long? Epoch, DateTimeOffset ExpiresAt);
}
