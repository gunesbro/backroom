using System.Diagnostics.CodeAnalysis;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Backroom.ControlPlane.Contracts.V1;
using Backroom.Modules.Realtime.Contracts;

namespace Backroom.Modules.ControlPlaneConnector.Infrastructure.Link;

/// <summary>
/// One <c>FullSync</c> call (docs/contracts/phase-1.md, "Drift repair"): the revoked sessions the control plane holds for
/// accounts that are members in this cell are applied through <see cref="ISessionRevocation"/>, which ignores expired ones and
/// is idempotent, so a session whose <c>SessionRevoked</c> was lost is still refused. It never deletes anything in the cell. The
/// organizations in the answer are not used yet (the alert for unknown organizations comes with the drift repair of
/// memberships).
/// </summary>
internal sealed partial class FullSyncRunner(ISessionRevocation revocation, TimeProvider timeProvider, ILogger<FullSyncRunner> logger)
{
    /// <summary>The longest one call may take.</summary>
    internal static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(30);

    /// <summary>More revocations than this in one answer are ignored (the control plane sends at most 10,000).</summary>
    internal const int MaxRevocations = 10_000;

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
        return applied;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Applying a revocation from FullSync failed with {ExceptionType}.")]
    private static partial void LogRevocationFailed(ILogger logger, string exceptionType);

    [LoggerMessage(Level = LogLevel.Information, Message = "FullSync applied {Applied} revocations ({Failed} failed).")]
    private static partial void LogDone(ILogger logger, int applied, int failed);
}
