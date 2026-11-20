namespace Backroom.BuildingBlocks.IntegrationTests;

/// <summary>Tests that reset the shared tables; a collection runs its tests one at a time.</summary>
[CollectionDefinition(Name)]
public sealed class PostgresCollection
{
    public const string Name = "Postgres";
}
