namespace Backroom.Modules.Identity.Contracts;

/// <summary>Identity's read of account statuses for the link's status sync.</summary>
public interface IAccountStatusQueries
{
    /// <summary>The members of the cell one page looks at (and so the most statuses it carries: about 150 KB, far below the link's 4 MB).</summary>
    public const int PageSize = 1000;

    /// <summary>
    /// One page of the statuses of accounts that are members of an organization in <paramref name="cellId"/>, in account id order after
    /// <paramref name="after"/> (<see cref="Guid.Empty"/> for the first page); never another account's. Expired parts are left out, and a
    /// status with nothing in force is skipped. <c>Next</c> is the cursor of the next page, or <see langword="null"/> after the last.
    /// </summary>
    Task<(IReadOnlyList<AccountStatus> Statuses, Guid? Next)> ListPageForCellAsync(string cellId, Guid after, CancellationToken cancellationToken);
}
