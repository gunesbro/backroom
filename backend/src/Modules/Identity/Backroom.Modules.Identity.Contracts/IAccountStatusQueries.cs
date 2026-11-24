namespace Backroom.Modules.Identity.Contracts;

/// <summary>Identity's read of account statuses for <c>FullSync</c>.</summary>
public interface IAccountStatusQueries
{
    /// <summary>The most statuses one answer carries (about 1.2 MB with the zone and a short text; the link's message limit is 4 MB).</summary>
    public const int MaxPerCell = 10_000;

    /// <summary>
    /// The statuses of accounts that are members of an organization in <paramref name="cellId"/>, at most <see cref="MaxPerCell"/>; never
    /// another account's, so a cell learns nothing about people it does not serve.
    /// </summary>
    Task<IReadOnlyList<AccountStatus>> ListForCellAsync(string cellId, CancellationToken cancellationToken);
}
