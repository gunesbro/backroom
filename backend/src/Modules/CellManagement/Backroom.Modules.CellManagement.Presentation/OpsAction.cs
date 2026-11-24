namespace Backroom.Modules.CellManagement.Presentation;

/// <summary>Marks an endpoint as an operations call and names its audit action.</summary>
internal sealed record OpsAction(string Name);
