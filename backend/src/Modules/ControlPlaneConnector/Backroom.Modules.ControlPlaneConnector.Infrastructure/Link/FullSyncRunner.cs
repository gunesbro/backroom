using System.Diagnostics.CodeAnalysis;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Backroom.ControlPlane.Contracts.V1;
using Backroom.Modules.Presence.Contracts;
using Backroom.Modules.Realtime.Contracts;

namespace Backroom.Modules.ControlPlaneConnector.Infrastructure.Link;

/// <summary>
/// One <c>FullSync</c> call (docs/contracts/phase-1.md, "Drift repair"): the revoked sessions the control plane holds for
/// accounts that are members in this cell are applied through <see cref="ISessionRevocation"/>, which ignores expired ones and
/// is idempotent, so a session whose <c>SessionRevoked</c> was lost is still refused. The account statuses of the cell's members go to
/// Presence, which keeps only newer versions (docs/contracts/phase-2.md), so a lost <c>AccountStatusChanged</c> or an emptied Valkey is
/// repaired. It never deletes anything in the cell. The organizations in the answer are not used yet (the alert for unknown
/// organizations comes with the drift repair of memberships).
/// </summary>
internal sealed partial class FullSyncRunner(ISessionRevocation revocation, IAccountStatusSink statuses, TimeProvider timeProvider, ILogger<FullSyncRunner> logger)
{
    /// <summary>The longest one call may take.</summary>
    internal static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(30);

    /// <summary>More revocations than this in one answer are ignored (the control plane sends at most 10,000).</summary>
    internal const int MaxRevocations = 10_000;

    /// <summary>Status pages one run reads at most (1,000 members each), so a run is bounded however large the cell is.</summary>
    internal const int MaxStatusPages = 100;

    /// <summary>More statuses than this in one page are ignored (the control plane sends at most 1,000).</summary>
    internal const int MaxStatusesPerPage = 1000;

    /// <summary>Calls <c>FullSync</c> with the stream's token and applies the revocations; returns how many were applied.</summary>
    [SuppressMessage("Design", "CA1031", Justification = "One malformed or failing revocation must not keep the others from being applied.")]
    public async Task<int> RunAsync(CellLinkService.CellLinkServiceClient client, string token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        using var deadline = new CancellationTokenSource(CallTimeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var response = await client.FullSyncAsync(
            new FullSyncRequest(), new Metadata { { "Authorization", "Bearer " + token } }, cancellationToken: linked.Token);

        var applied = 0;
        var failed = 0;
        foreach (var message in response.RevokedSessions.Take(MaxRevocations))
        {
            try
            {
                if (!SessionRevokedHandler.TryRead(message, out var item))
                {
                    failed++;
                    continue;
                }

                await revocation.RevokeAsync(item, cancellationToken);
                applied++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                failed++;
                LogRevocationFailed(logger, exception.GetType().Name);
            }
        }

        LogDone(logger, applied, failed);

        // Statuses come after the revocations, through their own paged call: whatever happens to them, the revocations are applied.
        try
        {
            await SyncStatusesAsync(client, token, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogStatusSyncFailed(logger, exception.GetType().Name);
        }

        return applied;
    }

    /// <summary>Reads the account statuses of the cell's members page by page and hands them to Presence (only newer versions are kept).</summary>
    [SuppressMessage("Design", "CA1031", Justification = "One malformed or failing status must not keep the others from being applied.")]
    internal async Task<int> SyncStatusesAsync(CellLinkService.CellLinkServiceClient client, string token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        var applied = 0;
        var failed = 0;
        var after = string.Empty;
        for (var page = 0; page < MaxStatusPages; page++)
        {
            using var deadline = new CancellationTokenSource(CallTimeout, timeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
            var response = await client.SyncAccountStatusesAsync(
                new AccountStatusSyncRequest { AfterAccountId = after }, new Metadata { { "Authorization", "Bearer " + token } }, cancellationToken: linked.Token);
            foreach (var message in response.Statuses.Take(MaxStatusesPerPage))
            {
                try
                {
                    if (!AccountStatusChangedHandler.TryRead(message, out var status))
                    {
                        failed++;
                        continue;
                    }

                    // Only a newer version is stored; the same one just keeps the copy from expiring.
                    await statuses.ApplyAsync(status, cancellationToken);
                    applied++;
                }
                catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    failed++;
                    LogStatusFailed(logger, exception.GetType().Name);
                }
            }

            // The cursor must move forward, or a faulty answer could keep the loop on one page.
            if (response.NextAfterAccountId.Length == 0 || string.CompareOrdinal(response.NextAfterAccountId, after) <= 0)
            {
                break;
            }

            after = response.NextAfterAccountId;
        }

        LogStatusesDone(logger, applied, failed);
        return applied;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Applying a revocation from FullSync failed with {ExceptionType}.")]
    private static partial void LogRevocationFailed(ILogger logger, string exceptionType);

    [LoggerMessage(Level = LogLevel.Information, Message = "FullSync applied {Applied} revocations ({Failed} failed).")]
    private static partial void LogDone(ILogger logger, int applied, int failed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Applying an account status from the status sync failed with {ExceptionType}.")]
    private static partial void LogStatusFailed(ILogger logger, string exceptionType);

    [LoggerMessage(Level = LogLevel.Information, Message = "The status sync checked {Applied} account statuses ({Failed} failed).")]
    private static partial void LogStatusesDone(ILogger logger, int applied, int failed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The account status sync failed with {ExceptionType}; the next FullSync tries again.")]
    private static partial void LogStatusSyncFailed(ILogger logger, string exceptionType);
}
