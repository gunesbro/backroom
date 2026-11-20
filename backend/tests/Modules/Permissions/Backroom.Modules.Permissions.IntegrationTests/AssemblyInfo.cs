using Backroom.Testing.Postgres;

// One PostgreSQL container for the assembly.
[assembly: AssemblyFixture(typeof(PostgresDatabase))]
