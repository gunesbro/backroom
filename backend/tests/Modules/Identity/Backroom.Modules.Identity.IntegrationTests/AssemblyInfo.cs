using Backroom.Testing.Postgres;

// One PostgreSQL container for the assembly; the Keycloak test class starts its own Keycloak.
[assembly: AssemblyFixture(typeof(PostgresDatabase))]
