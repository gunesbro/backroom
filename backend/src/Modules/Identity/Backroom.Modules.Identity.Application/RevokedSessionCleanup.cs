using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Backroom.BuildingBlocks.Application.Persistence;

namespace Backroom.Modules.Identity.Application;

/// <summary>
/// Deletes revocations after their <c>keep_until</c> (docs/contracts/phase-1.md, "Data"): by then every token that could
/// have been issued before the revocation has expired. Each batch is its own short unit of work, a run stops after
/// <see cref="MaxBatchesPerRun"/> batches, and several instances may run it at once (rows another one holds are skipped).
/// </summary>
internal sealed partial class RevokedSessionCleanup(IServiceScopeFactory scopes, TimeProvider timeProvider, ILogger<RevokedSessionCleanup> logger)
{
    public const int BatchSize = 500;

    public const int MaxBatchesPerRun = 20;

    public Task<int> RunAsync(CancellationToken cancellationToken) => RunAsync(BatchSize, MaxBatchesPerRun, cancellationToken);

    internal async Task<int> RunAsync(int batchSize, int maxBatches, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBatches);
        var now = timeProvider.GetUtcNow();
        var total = 0;
        for (var round = 0; round < maxBatches; round++)
        {
            int deleted;
            await using (var scope = scopes.CreateAsyncScope())
            {
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                await unitOfWork.BeginAsync(cancellationToken);
                deleted = await scope.ServiceProvider.GetRequiredService<IRevokedSessionStore>().DeleteExpiredAsync(now, batchSize, cancellationToken);
                await unitOfWork.CommitAsync(cancellationToken);
            }

            total += deleted;
            if (deleted < batchSize)
            {
                break;
            }
        }

        if (total > 0)
        {
            LogDeleted(logger, total);
        }

        return total;
    }

    [LoggerMessage(EventId = 10, Level = LogLevel.Information, Message = "Deleted {Count} expired session revocations.")]
    private static partial void LogDeleted(ILogger logger, int count);
}
