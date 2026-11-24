using Backroom.Modules.Identity.Contracts;

namespace Backroom.Modules.Identity.Application;

/// <summary>The control plane's account statuses (<c>identity.account_status</c>), in the scope's unit of work.</summary>
internal interface IAccountStatusStore
{
    Task<AccountStatus?> GetAsync(Guid accountId, CancellationToken cancellationToken);

    /// <summary>Replaces the account's status and returns it with its new version (one more than the stored one, or 1).</summary>
    Task<AccountStatus> SaveAsync(Guid accountId, AccountStatusInput status, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>The stored statuses of <paramref name="accountIds"/> (at most 5,000), on a connection of its own (no unit of work); accounts without one are left out.</summary>
    Task<IReadOnlyList<AccountStatus>> ListAsync(IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken);
}
