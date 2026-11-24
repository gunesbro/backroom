using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;
using Backroom.Testing.Valkey;

// One PostgreSQL, one Keycloak and one Valkey container for the assembly.
[assembly: AssemblyFixture(typeof(PostgresDatabase))]
[assembly: AssemblyFixture(typeof(KeycloakRealm))]
[assembly: AssemblyFixture(typeof(ValkeyServer))]
