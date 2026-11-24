namespace Backroom.Modules.CellManagement.Presentation;

/// <summary>The cell's epoch after an increment, as a string (the form cursors carry it in, docs/contracts/phase-2.md).</summary>
public sealed record CellEpochResponse(string Id, string Epoch);
