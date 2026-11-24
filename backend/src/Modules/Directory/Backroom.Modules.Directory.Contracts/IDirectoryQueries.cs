namespace Backroom.Modules.Directory.Contracts;

/// <summary>
/// Directory's reads for other control plane modules (docs/contracts/phase-1.md, "Cell ↔ control plane link"):
/// where an account has a membership and what a cell holds. Every list is bounded.
/// </summary>
public interface IDirectoryQueries
{
    /// <summary>The most cells an account is listed in; an account that belongs to more is cut off (and logged by the caller).</summary>
    public const int MaxCellsPerAccount = 1000;

    /// <summary>The most accounts <see cref="SelectMembersOfCellAsync"/> takes at a time.</summary>
    public const int MaxAccountsPerSelection = 10_000;

    /// <summary>The ids of the cells where the account has a pending or active membership, in order, at most <see cref="MaxCellsPerAccount"/>.</summary>
    Task<IReadOnlyList<string>> ListCellsOfAccountAsync(Guid accountId, CancellationToken cancellationToken);

    /// <summary>The accounts among <paramref name="accountIds"/> (at most <see cref="MaxAccountsPerSelection"/>) that have a pending or active membership in an organization of the cell.</summary>
    Task<IReadOnlyList<Guid>> SelectMembersOfCellAsync(string cellId, IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken);

    /// <summary>The organizations assigned to the cell that did not fail, oldest first, at most <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<CellOrganization>> ListOrganizationsOfCellAsync(string cellId, int limit, CancellationToken cancellationToken);
}
