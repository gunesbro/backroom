using Backroom.Modules.Presence.Contracts;

namespace Backroom.Modules.Presence.Infrastructure;

/// <summary>
/// <see cref="IAccountStatusSink"/>: stores the link's account status copy in Valkey when its version is newer (outside row-level
/// security, owner decision 16; read only through <see cref="IPresenceReader"/>). A stored change makes the person due, so an app
/// node's sweep publishes it to the organizations where someone watches them. Valkey failures throw, so the link retries.
/// </summary>
internal sealed class AccountStatusSink(PresenceStore store, TimeProvider timeProvider) : IAccountStatusSink
{
    public Task<bool> ApplyAsync(AccountStatusCopy status, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentOutOfRangeException.ThrowIfEqual(status.AccountId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(status.Version);
        return store.ApplyStatusAsync(status, timeProvider.GetUtcNow(), cancellationToken);
    }
}
