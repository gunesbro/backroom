namespace Backroom.Modules.CellManagement.Presentation;

/// <summary>A shared cell: its id names its scope <c>cell:{id}</c>. Nothing else about the cell is public.</summary>
public sealed record SharedCell(string Id);
