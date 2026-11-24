namespace Backroom.Modules.Realtime.IntegrationTests;

/// <summary>
/// The tests that start cells: each creates its cell's client scope in the shared Keycloak realm, and Keycloak answers
/// 409 when two requests change the realm at the same time, so these classes do not run in parallel.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public static class CellCollection
{
    public const string Name = "Cell";
}
