using Backroom.Modules.Directory.Contracts;
using Backroom.Modules.Identity.Contracts;

namespace Backroom.Modules.Identity.Application;

/// <summary>
/// <c>FullSync</c>'s account statuses: the cell's members are read from Directory in pages by account id and their statuses looked up
/// by primary key, so the work grows with the cell's members, not with every account of the control plane. At most
/// <see cref="IAccountStatusQueries.MaxPerCell"/> statuses and <see cref="MaxPages"/> pages of members. The stored values are sent as
/// they are (expired ones too, so the cell's copy has the newest version); the cell leaves expired parts out when it reads them.
/// </summary>
internal sealed class AccountStatusQueries(IAccountStatusStore store, IDirectoryQueries directory) : IAccountStatusQueries
{
    public const int PageSize = IDirectoryQueries.MaxAccountsPerPage;

    public const int MaxPages = 10;

    public async Task<IReadOnlyList<AccountStatus>> ListForCellAsync(string cellId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(cellId);
        var result = new List<AccountStatus>();
        var after = Guid.Empty;

        for (var page = 0; page < MaxPages && result.Count < IAccountStatusQueries.MaxPerCell; page++)
        {
            var accounts = await directory.ListAccountsOfCellAsync(cellId, after, PageSize, cancellationToken);
            if (accounts.Count == 0)
            {
                break;
            }

            result.AddRange(await store.ListAsync(accounts, cancellationToken));
            if (accounts.Count < PageSize)
            {
                break;
            }

            after = accounts[^1];
        }

        return result.Count <= IAccountStatusQueries.MaxPerCell ? result : result[..IAccountStatusQueries.MaxPerCell];
    }
}
