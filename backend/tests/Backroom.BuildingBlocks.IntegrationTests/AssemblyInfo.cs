using Backroom.Testing.Postgres;
using Backroom.Testing.RabbitMq;

// One PostgreSQL and one RabbitMQ container for the assembly.
[assembly: AssemblyFixture(typeof(PostgresDatabase))]
[assembly: AssemblyFixture(typeof(RabbitMqBroker))]
