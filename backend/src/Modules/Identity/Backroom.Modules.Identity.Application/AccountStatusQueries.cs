using Backroom.Modules.Directory.Contracts;
using Backroom.Modules.Identity.Contracts;

namespace Backroom.Modules.Identity.Application;

/// <summary>
/// The link's status sync: one page of the cell's members from Directory (keyset by account id) and their statuses by primary key, so
/// the work grows with the cell's members, not with every account of the control plane, and each answer is bounded on its own. Expired
/// parts are left out and empty statuses skipped; the version stays, so the cell still keeps only newer copies.
/// </summary>
internal sealed class AccountStatusQueries(IAccountStatusStore store, IDirectoryQueries directory, TimeProvider timeProvider) : IAccountStatusQueries
{
    public async Task<(IReadOnlyList<AccountStatus> Statuses, Guid? Next)> ListPageForCellAsync(string cellId, Guid after, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(cellId);
        var accounts = await directory.ListAccountsOfCellAsync(cellId, after, IAccountStatusQueries.PageSize, cancellationToken);
        if (accounts.Count == 0)
        {
            return ([], null);
        }

        var now = timeProvider.GetUtcNow();
        var statuses = (await store.ListAsync(accounts, cancellationToken))
            .Select(status => AccountStatusRules.Effective(status, now))
            .Where(status => status.DndUntil is not null || status.CustomEmoji is not null || status.CustomText is not null || status.TimeZone is not null)
            .ToList();
        return (statuses, accounts.Count < IAccountStatusQueries.PageSize ? null : accounts[^1]);
    }
}
