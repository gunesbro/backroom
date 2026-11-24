using Backroom.Modules.Identity.Contracts;

namespace Backroom.Modules.Identity.Application;

/// <summary>The control plane's revoked sessions (<c>identity.revoked_sessions</c>), in the scope's unit of work.</summary>
internal interface IRevokedSessionStore
{
    /// <summary>
    /// Records the revocation. Returns <see langword="false"/> when a revocation of the same session (<c>sid</c>) was already
    /// recorded, so a repeated logout token (Keycloak does not retry, but a delivery can be duplicated) is not sent to the cells again. A revocation of
    /// every session of an account (no <c>sid</c>) replaces an older one and returns <see langword="true"/>.
    /// </summary>
    Task<bool> TryRecordAsync(RevokedSession revocation, CancellationToken cancellationToken);

    /// <summary>
    /// The revocations still in force at <paramref name="now"/>, newest first, at most <paramref name="limit"/>, those after <paramref name="after"/>
    /// in that order (a page cursor; <see langword="null"/> for the first page).
    /// </summary>
    Task<IReadOnlyList<RevokedSession>> ListInForceAsync(DateTimeOffset now, int limit, RevokedSession? after, CancellationToken cancellationToken);

    /// <summary>Deletes up to <paramref name="batchSize"/> revocations whose <c>keep_until</c> is not after <paramref name="now"/>; returns how many it deleted.</summary>
    Task<int> DeleteExpiredAsync(DateTimeOffset now, int batchSize, CancellationToken cancellationToken);
}
