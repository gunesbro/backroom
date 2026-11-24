namespace Backroom.Modules.CellManagement.Application.Listing;

/// <summary>One page of cells in id order; <see cref="Next"/> is the cursor of the following page, or <see langword="null"/>.</summary>
internal sealed record CellPage(IReadOnlyList<CellSummary> Cells, string? Next);
