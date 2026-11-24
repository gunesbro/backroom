using System.Diagnostics;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Backroom.BuildingBlocks.Application.Persistence;
using Backroom.ControlPlane.Contracts.V1;
using Backroom.Modules.CellManagement.Contracts;
using Backroom.Modules.Directory.Contracts;
using Backroom.Modules.Identity.Contracts;

namespace Backroom.Modules.Identity.Application;

/// <summary>
/// Records a revocation and tells the cells (docs/contracts/phase-1.md, flow 5): in one unit of work the revoked session
/// is stored and a <c>SessionRevoked</c> message is queued for every cell where the account has a membership, so the cells
/// hear of it if and only if it was recorded. It is kept until <c>revoked_at</c> plus the longest access token lifetime and
/// the clock skew (<see cref="SessionRevocationPolicy.KeepFor"/>). Repeating the same logout is harmless: a session
/// already recorded is not sent again.
/// </summary>
internal sealed partial class SessionRevocations(
    IUnitOfWork unitOfWork,
    IRevokedSessionStore store,
    IDirectoryQueries directory,
    ICellLinkOutbox outbox,
    TimeProvider timeProvider,
    ILogger<SessionRevocations> logger)
{
    public const string ActivitySourceName = "Backroom.Identity";

    /// <summary>The longest session id stored and sent (Keycloak's are UUIDs).</summary>
    public const int MaxSessionIdLength = 128;

    private static readonly ActivitySource s_activitySource = new(ActivitySourceName);

    /// <summary>Revokes <paramref name="sessionId"/> of the account, or every session of it when <see langword="null"/>; returns how many cells were told.</summary>
    public async Task<int> RevokeAsync(Guid accountId, string? sessionId, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(accountId, Guid.Empty);
        if (sessionId is { Length: 0 or > MaxSessionIdLength })
        {
            throw new ArgumentException("The session id must be 1 to 128 characters.", nameof(sessionId));
        }

        using var activity = s_activitySource.StartActivity("session revocation");
        var revokedAt = timeProvider.GetUtcNow();
        var revocation = new RevokedSession(accountId, sessionId, revokedAt, revokedAt + SessionRevocationPolicy.KeepFor);

        // Membership is read before the transaction, on another connection; a membership that appears right now is
        // covered by FullSync.
        var cells = await directory.ListCellsOfAccountAsync(accountId, cancellationToken);
        var told = 0;
        await unitOfWork.BeginAsync(cancellationToken);
        try
        {
            if (!await store.TryRecordAsync(revocation, cancellationToken))
            {
                await unitOfWork.RollbackAsync(CancellationToken.None);
                LogRepeated(logger, accountId);
                return 0;
            }

            foreach (var cellId in cells)
            {
                await outbox.EnqueueAsync(cellId, new ConnectResponse { SessionRevoked = ToMessage(revocation) }, cancellationToken);
                told++;
            }

            await unitOfWork.CommitAsync(cancellationToken);
        }
        catch
        {
            await unitOfWork.RollbackAsync(CancellationToken.None);
            throw;
        }

        activity?.SetTag("identity.revocation.cells", told);
        LogRevoked(logger, accountId, told, sessionId is null);
        return told;
    }

    internal static SessionRevoked ToMessage(RevokedSession revocation) => new()
    {
        AccountId = revocation.AccountId.ToString("D"),
        SessionId = revocation.SessionId ?? string.Empty,
        RevokedAt = Timestamp.FromDateTimeOffset(revocation.RevokedAt),
        KeepUntil = Timestamp.FromDateTimeOffset(revocation.KeepUntil),
    };

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Security: a session of account {AccountId} was revoked and sent to {Cells} cells (all sessions: {AllSessions}).")]
    private static partial void LogRevoked(ILogger logger, Guid accountId, int cells, bool allSessions);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "A repeated logout of a revoked session of account {AccountId} was ignored.")]
    private static partial void LogRepeated(ILogger logger, Guid accountId);
}
