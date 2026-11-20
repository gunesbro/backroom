using Backroom.Testing.Mailpit;
using Backroom.Testing.Postgres;

// One PostgreSQL and one Mailpit container for the assembly.
[assembly: AssemblyFixture(typeof(PostgresDatabase))]
[assembly: AssemblyFixture(typeof(MailpitServer))]
