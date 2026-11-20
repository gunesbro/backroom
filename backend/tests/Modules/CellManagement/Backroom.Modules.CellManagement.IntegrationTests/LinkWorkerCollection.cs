namespace Backroom.Modules.CellManagement.IntegrationTests;

/// <summary>
/// Classes that run a cell's link worker against the shared test database. The cell outbox is one table
/// for all test cells, so these classes run one at a time: a worker of one class must not send another
/// class's messages to its own control plane.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public static class LinkWorkerCollection
{
    public const string Name = "Link workers";
}
