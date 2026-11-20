namespace Backroom.Modules.CellManagement.Infrastructure;

/// <summary>
/// Development only: cells the AppHost runs, with the registration key it generated for each
/// (docs/contracts/phase-1.md, "Cell registration", step 1). Ignored outside Development.
/// </summary>
internal sealed class DevelopmentCellOptions
{
    public const string SectionName = "CellManagement";

    public IList<DevelopmentCell> DevelopmentCells { get; } = [];
}
