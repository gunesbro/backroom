namespace Backroom.Modules.CellManagement.Contracts;

/// <summary>A cell's id and the public URL clients use to reach it.</summary>
public sealed record CellAddress(string Id, Uri PublicUrl);
