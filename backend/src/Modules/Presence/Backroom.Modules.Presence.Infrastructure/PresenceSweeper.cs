using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Backroom.BuildingBlocks.Application.Organizations;
using Backroom.BuildingBlocks.Application.Persistence;

namespace Backroom.Modules.Presence.Infrastructure;

/// <summary>
/// The app node's presence timer (one per node, every <see cref="PresenceOptions.SweepInterval"/>): renews this node's connections
/// every <see cref="PresenceOptions.RefreshInterval"/> and its watches at half their lifetime, and evaluates the people who are due (a
/// connection expired because its node is gone, the idle time passed, a coalesced event is ready, a status arrived). The due set is
/// shared, and a claim leases each person to one node, so the work is split between the nodes and bounded per sweep.
/// </summary>
internal sealed partial class PresenceSweeper(
    PresenceTracker tracker,
    PresenceStore store,
    PresencePublisher publisher,
    IServiceScopeFactory scopes,
    IOptions<PresenceOptions> options,
    TimeProvider timeProvider,
    ILogger<PresenceSweeper> logger) : BackgroundService
{
    private DateTimeOffset _lastRefresh = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(options.Value.SweepInterval, timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RunOnceAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    /// <summary>One sweep: renewals when due, then the evaluations due now. Never throws for Valkey failures.</summary>
    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var now = timeProvider.GetUtcNow();
        try
        {
            if (now - _lastRefresh >= settings.RefreshInterval)
            {
                _lastRefresh = now;
                await store.RefreshAsync(tracker.Snapshot(), (now + settings.OfflineAfter).ToUnixTimeMilliseconds(), cancellationToken);
            }

            await RenewWatchesAsync(tracker.TakeWatchesToRenew(now - settings.WatchLifetime / 2, now), now, cancellationToken);

            var due = await store.ClaimDueAsync(now, settings.SweepBatchSize, cancellationToken);
            foreach (var accountId in due)
            {
                await publisher.EvaluateAsync(accountId, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            PresenceTelemetry.Failures.Add(1, new KeyValuePair<string, object?>("operation", "sweep"));
            LogSweepFailed(logger, exception.GetType().Name);
        }
    }

    // A watch is renewed only for people who are still active members of its organization (one statement per organization and
    // batch of 1,000), so a removal whose broadcast was lost cannot bring the person back; the others are dropped from the watch sets.
    private async Task RenewWatchesAsync(List<(Guid OrganizationId, Guid[] AccountIds)> watches, DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var organization in watches.GroupBy(watch => watch.OrganizationId))
        {
            var wanted = organization.SelectMany(watch => watch.AccountIds).Distinct().ToList();
            foreach (var batch in wanted.Chunk(IOrganizationMembershipCheck.MaxAccountsPerSelection))
            {
                var members = (await ActiveMembersAsync(organization.Key, batch, cancellationToken)).ToHashSet();
                foreach (var gone in batch.Where(accountId => !members.Contains(accountId)))
                {
                    tracker.Unwatch(organization.Key, gone);
                }

                await store.WatchAsync(organization.Key, members, now, cancellationToken);
            }
        }
    }

    private async Task<IReadOnlyList<Guid>> ActiveMembersAsync(Guid organizationId, IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<OrganizationContext>().Enter(organizationId);
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await unitOfWork.BeginAsync(cancellationToken);
        var members = await scope.ServiceProvider.GetRequiredService<IOrganizationMembershipCheck>().SelectActiveMembersAsync(accountIds, cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);
        return members;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "A presence sweep failed ({ExceptionType}); the next one tries again.")]
    private static partial void LogSweepFailed(ILogger logger, string exceptionType);
}
