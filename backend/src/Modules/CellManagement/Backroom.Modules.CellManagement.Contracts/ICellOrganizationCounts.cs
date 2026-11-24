namespace Backroom.Modules.CellManagement.Contracts;

/// <summary>How many organizations live in each cell; implemented by Directory, the source of that mapping. Used by the operations panel.</summary>
public interface ICellOrganizationCounts
{
    /// <summary>At most this many cells per lookup.</summary>
    public const int MaxCellsPerLookup = 1_000;

    /// <summary>The number of organizations of each cell in <paramref name="cellIds"/>; cells without organizations are left out.</summary>
    Task<IReadOnlyDictionary<string, int>> CountByCellAsync(IReadOnlyCollection<string> cellIds, CancellationToken cancellationToken);
}
