namespace Backroom.Modules.CellManagement.Contracts;

/// <summary>
/// Which organizations live in which cell; implemented by Directory, the source of that mapping. The link
/// accepts an upstream message only if every organization it names belongs to the sending cell.
/// </summary>
public interface ICellOrganizationScope
{
    /// <summary>At most this many organizations per check (and per upstream message; a digest is sent in chunks).</summary>
    public const int MaxOrganizationsPerCheck = 10_000;

    /// <summary>Whether every organization in <paramref name="organizationIds"/> is mapped to <paramref name="cellId"/>.</summary>
    Task<bool> AreInCellAsync(IReadOnlyCollection<Guid> organizationIds, string cellId, CancellationToken cancellationToken);
}
