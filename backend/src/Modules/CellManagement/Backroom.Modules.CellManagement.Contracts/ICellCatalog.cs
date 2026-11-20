namespace Backroom.Modules.CellManagement.Contracts;

/// <summary>Where cells are reached, for modules that send clients to a cell.</summary>
public interface ICellCatalog
{
    /// <summary>
    /// The registered cells among <paramref name="cellIds"/>, by id; unknown and pending cells are left out.
    /// At most <see cref="MaxCellsPerLookup"/> ids per call.
    /// </summary>
    Task<IReadOnlyDictionary<string, CellAddress>> GetRegisteredCellsAsync(IReadOnlyCollection<string> cellIds, CancellationToken cancellationToken);

    public const int MaxCellsPerLookup = 500;

    /// <summary>A cell whose link sent no heartbeat for this long is not healthy.</summary>
    public static readonly TimeSpan HealthyHeartbeatAge = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The shared cells of <paramref name="region"/> that can take new organizations: registered and with a
    /// link heartbeat in the last <see cref="HealthyHeartbeatAge"/>. At most <see cref="MaxCellsPerLookup"/>.
    /// </summary>
    Task<IReadOnlyList<CellAddress>> GetHealthyCellsAsync(string region, CancellationToken cancellationToken);
}
