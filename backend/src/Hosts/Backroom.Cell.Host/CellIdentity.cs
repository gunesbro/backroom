namespace Backroom.Cell.Host;

/// <summary>The cell's id, assigned at registration; tokens for this cell carry the audience <c>cell-{id}</c>.</summary>
internal static class CellIdentity
{
    public const string ConfigurationKey = "Cell:Id";
}
