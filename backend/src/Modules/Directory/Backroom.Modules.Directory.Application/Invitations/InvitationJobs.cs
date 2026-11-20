using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Backroom.BuildingBlocks.Application.Persistence;

namespace Backroom.Modules.Directory.Application.Invitations;

/// <summary>
/// The control plane's scheduled invitation work (docs/contracts/phase-1.md, flow 3, step 8, and "Privacy and
/// retention"): pending invitations become <c>expired</c> at <c>expires_at</c>; the email HMAC of expired, revoked
/// and failed invitations is deleted 30 days after they ended, and of used ones 30 days after use; revocation
/// tombstones are deleted once a late registration of their invitation would be refused anyway. Each batch is its
/// own short unit of work, and a run stops after <see cref="MaxBatchesPerRun"/> batches; whatever is left waits for
/// the next run. Safe with several instances: busy rows are skipped, never waited for.
/// </summary>
/// <remarks>
/// The jobs use the application clock (<see cref="TimeProvider"/>), not the database's: acceptance, resolution and
/// registration decide expiry by that clock, so the job agrees with them, and tests move it with a fake clock.
/// </remarks>
internal sealed partial class InvitationJobs(IServiceScopeFactory scopes, TimeProvider timeProvider, ILogger<InvitationJobs> logger)
{
    public const string ActivitySourceName = "Backroom.Directory";

    /// <summary>Rows per statement: also the advisory locks a batch tries.</summary>
    public const int BatchSize = 250;

    /// <summary>At most 10,000 rows per job and run.</summary>
    public const int MaxBatchesPerRun = 40;

    /// <summary>How long ended and used invitations keep their email HMAC.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    /// <summary>
    /// How long a revocation tombstone stays: an invitation expires at most <see cref="InvitationLinkMessages.MaxLifetime"/>
    /// after its revocation (it was created before), and a registration whose expiry is more than
    /// <see cref="InvitationLinkMessages.MaxExpiryAge"/> past is refused; so after both, a late registration can no
    /// longer bring the invitation back.
    /// </summary>
    public static readonly TimeSpan TombstoneRetention = InvitationLinkMessages.MaxExpiryAge + InvitationLinkMessages.MaxLifetime;

    private static readonly ActivitySource s_activitySource = new(ActivitySourceName);

    /// <summary>Marks due pending invitations expired; returns how many.</summary>
    public Task<int> ExpireAsync(CancellationToken cancellationToken) => ExpireAsync(BatchSize, MaxBatchesPerRun, cancellationToken);

    internal async Task<int> ExpireAsync(int batchSize, int maxBatches, CancellationToken cancellationToken)
    {
        using var activity = s_activitySource.StartActivity("invitation expiry");
        var now = timeProvider.GetUtcNow();
        var expired = await RunInBatchesAsync(
            (registry, token) => registry.ExpireDueAsync(now, batchSize, token),
            batch =>
            {
                // Bounded by the batch and run limits.
                foreach (var invitation in batch.Expired)
                {
                    LogInvitationExpired(logger, invitation.InvitationId, invitation.OrganizationId);
                }
            },
            batchSize,
            maxBatches,
            cancellationToken);
        activity?.SetTag("directory.invitations.expired", expired);
        if (expired > 0)
        {
            LogExpired(logger, expired);
        }

        return expired;
    }

    /// <summary>Deletes the email HMACs and the tombstones past their retention; returns how many of each.</summary>
    public Task<(int EmailHmacsCleared, int TombstonesDeleted)> ApplyRetentionAsync(CancellationToken cancellationToken) =>
        ApplyRetentionAsync(BatchSize, MaxBatchesPerRun, cancellationToken);

    internal async Task<(int EmailHmacsCleared, int TombstonesDeleted)> ApplyRetentionAsync(int batchSize, int maxBatches, CancellationToken cancellationToken)
    {
        using var activity = s_activitySource.StartActivity("invitation retention");
        var now = timeProvider.GetUtcNow();
        var cleared = await RunInBatchesAsync(
            (registry, token) => registry.ClearEndedEmailHmacsAsync(now - Retention, now, batchSize, token), null, batchSize, maxBatches, cancellationToken);
        var deleted = await RunInBatchesAsync(
            (registry, token) => registry.DeleteTombstonesAsync(now - TombstoneRetention, batchSize, token), null, batchSize, maxBatches, cancellationToken);
        activity?.SetTag("directory.invitations.email_hmacs_cleared", cleared);
        activity?.SetTag("directory.invitations.tombstones_deleted", deleted);
        if (cleared > 0 || deleted > 0)
        {
            LogRetention(logger, cleared, deleted);
        }

        return (cleared, deleted);
    }

    private async Task<int> RunInBatchesAsync(
        Func<IInvitationRegistry, CancellationToken, Task<InvitationJobBatch>> batch,
        Action<InvitationJobBatch>? committed,
        int batchSize,
        int maxBatches,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBatches);
        var total = 0;
        for (var round = 0; round < maxBatches; round++)
        {
            InvitationJobBatch result;
            await using (var scope = scopes.CreateAsyncScope())
            {
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                await unitOfWork.BeginAsync(cancellationToken);
                result = await batch(scope.ServiceProvider.GetRequiredService<IInvitationRegistry>(), cancellationToken);
                await unitOfWork.CommitAsync(cancellationToken);
            }

            committed?.Invoke(result);
            total += result.Changed;

            // Fewer selected than the limit: nothing more is due. Busy rows the batch skipped do not end the run.
            if (result.Selected < batchSize)
            {
                break;
            }
        }

        return total;
    }

    [LoggerMessage(EventId = 50, Level = LogLevel.Information, Message = "Marked {Count} invitations expired.")]
    private static partial void LogExpired(ILogger logger, int count);

    [LoggerMessage(EventId = 51, Level = LogLevel.Information, Message = "Invitation retention deleted {EmailHmacs} email HMACs and {Tombstones} revocation tombstones.")]
    private static partial void LogRetention(ILogger logger, int emailHmacs, int tombstones);

    [LoggerMessage(EventId = 53, Level = LogLevel.Information, Message = "Security: invitation {InvitationId} of organization {OrganizationId} expired.")]
    private static partial void LogInvitationExpired(ILogger logger, Guid invitationId, Guid organizationId);
}
