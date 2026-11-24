using Backroom.BuildingBlocks.Application.Persistence;
using Backroom.Modules.Directory.Contracts;
using Backroom.Modules.Identity.Contracts;

namespace Backroom.Modules.Identity.Application;

/// <summary>
/// <c>FullSync</c>'s revocations: those in force whose account is a member in the asking cell, newest first, at most
/// <see cref="IRevokedSessionQueries.MaxPerCell"/>. The in-force rows (the sign-outs of the last minutes) are read newest first in pages
/// and each page is filtered by Directory before the limit applies, so a cell keeps its members' newest revocations however many other
/// accounts have signed out. Modules cannot join each other's tables, hence the pages; at most <see cref="MaxPages"/> of them are read.
/// </summary>
internal sealed class RevokedSessionQueries(IUnitOfWork unitOfWork, IRevokedSessionStore store, IDirectoryQueries directory, TimeProvider timeProvider) : IRevokedSessionQueries
{
    public const int PageSize = 2000;

    public const int MaxPages = 10;

    public async Task<IReadOnlyList<RevokedSession>> ListForCellAsync(string cellId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(cellId);
        var result = new List<RevokedSession>();
        var now = timeProvider.GetUtcNow();
        RevokedSession? after = null;

        // A read: one unit of work for all pages, nothing is committed.
        await unitOfWork.BeginAsync(cancellationToken);
        try
        {
            for (var page = 0; page < MaxPages && result.Count < IRevokedSessionQueries.MaxPerCell; page++)
            {
                var rows = await store.ListInForceAsync(now, PageSize, after, cancellationToken);
                if (rows.Count == 0)
                {
                    break;
                }

                var accounts = rows.Select(row => row.AccountId).Distinct().ToList();
                var members = (await directory.SelectMembersOfCellAsync(cellId, accounts, cancellationToken)).ToHashSet();
                result.AddRange(rows.Where(row => members.Contains(row.AccountId)));
                if (rows.Count < PageSize)
                {
                    break;
                }

                after = rows[^1];
            }
        }
        finally
        {
            await unitOfWork.RollbackAsync(CancellationToken.None);
        }

        return result.Take(IRevokedSessionQueries.MaxPerCell).ToList();
    }
}
