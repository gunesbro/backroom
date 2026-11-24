using Backroom.Modules.Realtime.Contracts;

namespace Backroom.Modules.Realtime.Infrastructure.Revocation;

/// <summary>Where the cell keeps its revoked sessions: Valkey in the cell, a fake in tests.</summary>
internal interface IRevocationStore
{
    /// <summary>Stores the revocation until its <c>keep_until</c>. Throws when the store does not take it in time. An expired one is not stored.</summary>
    Task StoreAsync(SessionRevocation revocation, CancellationToken cancellationToken);

    /// <summary>What the store holds for the session (when there is one) and for the account. Throws when the store does not answer in time.</summary>
    Task<(RevocationRecord? Session, RevocationRecord? Account)> GetAsync(string? sessionId, Guid accountId, CancellationToken cancellationToken);
}
