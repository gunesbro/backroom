namespace Backroom.Modules.CellManagement.IntegrationTests;

/// <summary>
/// Tests that count the shared cells of the shared test database: other classes add cells, so these run one at a
/// time, never next to another class.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public static class SharedCellCountCollection
{
    public const string Name = "Shared cell count";
}
