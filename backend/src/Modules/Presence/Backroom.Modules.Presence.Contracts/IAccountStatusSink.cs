namespace Backroom.Modules.Presence.Contracts;

/// <summary>Takes account statuses from the link (the connector's <c>AccountStatusChanged</c> handler and <c>FullSync</c>).</summary>
public interface IAccountStatusSink
{
    /// <summary>
    /// Stores the copy if its version is newer than the one held; returns whether it did. Applying the same or an older version
    /// changes nothing (it only keeps the copy from expiring). Throws when Valkey cannot be written, so the link retries.
    /// </summary>
    Task<bool> ApplyAsync(AccountStatusCopy status, CancellationToken cancellationToken);
}
