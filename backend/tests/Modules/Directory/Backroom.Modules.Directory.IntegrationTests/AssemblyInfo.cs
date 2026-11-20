using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;

// One PostgreSQL and one Keycloak container for the assembly.
[assembly: AssemblyFixture(typeof(PostgresDatabase))]
[assembly: AssemblyFixture(typeof(KeycloakRealm))]
