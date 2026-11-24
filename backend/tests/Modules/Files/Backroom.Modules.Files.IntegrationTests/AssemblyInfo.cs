using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;
using Backroom.Testing.SeaweedFs;

// One PostgreSQL, one Keycloak and one SeaweedFS container for the assembly.
[assembly: AssemblyFixture(typeof(PostgresDatabase))]
[assembly: AssemblyFixture(typeof(KeycloakRealm))]
[assembly: AssemblyFixture(typeof(SeaweedFsServer))]
