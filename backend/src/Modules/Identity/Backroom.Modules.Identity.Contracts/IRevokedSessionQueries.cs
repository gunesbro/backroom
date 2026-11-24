namespace Backroom.Modules.Identity.Contracts;

/// <summary>Identity's read for <c>FullSync</c>.</summary>
public interface IRevokedSessionQueries
{
    /// <summary>The most revocations one answer carries; the oldest are cut first.</summary>
    public const int MaxPerCell = 10_000;

    /// <summary>
    /// The revocations still in force (<c>keep_until</c> in the future) of accounts that are members of an
    /// organization in <paramref name="cellId"/>; never another account's, so a cell learns nothing about people it does not serve.
    /// </summary>
    Task<IReadOnlyList<RevokedSession>> ListForCellAsync(string cellId, CancellationToken cancellationToken);
}
