using Backroom.BuildingBlocks.Application.Persistence;
using Backroom.Modules.Directory.Contracts;
using Backroom.Modules.Identity.Contracts;

namespace Backroom.Modules.Identity.Application;

/// <summary>
/// <c>FullSync</c>'s revocations: those in force whose account is a member in the asking cell. The table is small (rows
/// live minutes), so the in-force rows are read once and their accounts are filtered by Directory in one call.
/// </summary>
internal sealed class RevokedSessionQueries(IUnitOfWork unitOfWork, IRevokedSessionStore store, IDirectoryQueries directory, TimeProvider timeProvider) : IRevokedSessionQueries
{
    public async Task<IReadOnlyList<RevokedSession>> ListForCellAsync(string cellId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(cellId);
        IReadOnlyList<RevokedSession> inForce;
        await unitOfWork.BeginAsync(cancellationToken);
        try
        {
            inForce = await store.ListInForceAsync(timeProvider.GetUtcNow(), IDirectoryQueries.MaxAccountsPerSelection, cancellationToken);
        }
        finally
        {
            // A read: there is nothing to commit.
            await unitOfWork.RollbackAsync(CancellationToken.None);
        }

        if (inForce.Count == 0)
        {
            return [];
        }

        var accounts = inForce.Select(session => session.AccountId).Distinct().ToList();
        var members = (await directory.SelectMembersOfCellAsync(cellId, accounts, cancellationToken)).ToHashSet();
        return inForce.Where(session => members.Contains(session.AccountId)).Take(IRevokedSessionQueries.MaxPerCell).ToList();
    }
}
