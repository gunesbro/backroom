namespace Backroom.Modules.CellManagement.Presentation;

/// <summary>The registered shared cells; a client requests their scopes at sign-in.</summary>
public sealed record SharedCellsResponse(IReadOnlyList<SharedCell> Cells);
