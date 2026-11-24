namespace Backroom.Modules.CellManagement.Presentation;

/// <summary>One page of cells in id order; pass <c>next</c> as <c>after</c> to get the following page.</summary>
public sealed record CellListResponse(IReadOnlyList<CellSummaryResponse> Cells, string? Next);
