using Microsoft.Extensions.Logging;
using Backroom.Modules.Realtime.Contracts;

namespace Backroom.Modules.Realtime.Infrastructure.Revocation;

/// <summary>
/// <see cref="ISessionRevocation"/>: stores the revocation in Valkey until its <c>keep_until</c>, takes it into this node's cache,
/// then announces it to every app node, which close the matching connections. The order matters: a node that learns of it
/// from the announcement, or a request that misses its cache, already finds it in Valkey. Everything is idempotent.
/// </summary>
internal sealed partial class SessionRevocationService(
    IRevocationStore store,
    RevocationChecker checker,
    SessionRevocationBroadcast broadcast,
    TimeProvider timeProvider,
    ILogger<SessionRevocationService> logger) : ISessionRevocation
{
    /// <summary>Keycloak's revocations last minutes; more than a day is a mistake or an attack on the cell's memory.</summary>
    internal static readonly TimeSpan MaxKeepAfterRevocation = TimeSpan.FromDays(1);

    public async Task RevokeAsync(SessionRevocation revocation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(revocation);
        if (revocation.AccountId == Guid.Empty
            || revocation.SessionId is { Length: 0 or > ValkeyRevocationStore.MaxSessionIdLength }
            || revocation.KeepUntil < revocation.RevokedAt
            || revocation.KeepUntil - revocation.RevokedAt > MaxKeepAfterRevocation)
        {
            throw new ArgumentException("The revocation is not valid.", nameof(revocation));
        }

        if (revocation.KeepUntil <= timeProvider.GetUtcNow())
        {
            // Every token that could carry it has expired.
            return;
        }

        await store.StoreAsync(revocation, cancellationToken);
        checker.Learn(revocation);
        await broadcast.PublishAsync(revocation, cancellationToken);
        LogApplied(logger, revocation.AccountId, revocation.SessionId is null);
    }

    [LoggerMessage(EventId = 31, Level = LogLevel.Information, Message = "Security: a revocation for account {AccountId} was applied (all sessions: {AllSessions}).")]
    private static partial void LogApplied(ILogger logger, Guid accountId, bool allSessions);
}
