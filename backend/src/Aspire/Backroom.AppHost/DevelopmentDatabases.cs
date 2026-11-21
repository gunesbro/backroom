namespace Backroom.AppHost;

/// <summary>
/// Databases on the development PostgreSQL server and the roles the services use (the same names as
/// <c>DatabaseRoles</c> in BuildingBlocks.Infrastructure). Role passwords are generated and kept in the
/// AppHost's user secrets. Each host's <c>migrate</c> run creates the roles with the administrator
/// connection (development only) and applies its modules' migrations as the migrator before the host
/// starts; the host itself connects as the application role.
/// </summary>
internal sealed class DevelopmentDatabases
{
    private const string MigratorRole = "backroom_migrator";
    private const string ApplicationRole = "backroom_app";
    private const string RelayRole = "backroom_relay";
    private const string MaintenanceRole = "backroom_maintenance";

    private readonly IResourceBuilder<PostgresServerResource> _postgres;
    private readonly IResourceBuilder<ParameterResource> _migratorPassword;
    private readonly IResourceBuilder<ParameterResource> _applicationPassword;
    private readonly IResourceBuilder<ParameterResource> _relayPassword;
    private readonly IResourceBuilder<ParameterResource> _maintenancePassword;

    public DevelopmentDatabases(IDistributedApplicationBuilder builder, IResourceBuilder<PostgresServerResource> postgres)
    {
        _postgres = postgres;
        _migratorPassword = Password(builder, "postgres-migrator-password");
        _applicationPassword = Password(builder, "postgres-app-password");
        _relayPassword = Password(builder, "postgres-relay-password");
        _maintenancePassword = Password(builder, "postgres-maintenance-password");
    }

    /// <summary>
    /// Adds <paramref name="databaseName"/>, a run of <paramref name="migrate"/> that bootstraps the roles
    /// and applies migrations, and makes <paramref name="hosts"/> wait for it and connect as the
    /// application role.
    /// </summary>
    public void Add(string databaseName, IResourceBuilder<ProjectResource> migrate, params IResourceBuilder<ProjectResource>[] hosts)
    {
        var database = _postgres.AddDatabase(databaseName.Replace('_', '-') + "-db", databaseName);

        migrate
            .WithEnvironment("DOTNET_ENVIRONMENT", "Development")
            .WithEnvironment("DatabaseMigration__BootstrapConnectionString", database.Resource.ConnectionStringExpression)
            .WithEnvironment("DatabaseMigration__MigratorConnectionString", ConnectionString(databaseName, MigratorRole, _migratorPassword))
            .WithEnvironment("DatabaseMigration__MigratorPassword", _migratorPassword)
            .WithEnvironment("DatabaseMigration__ApplicationPassword", _applicationPassword)
            .WithEnvironment("DatabaseMigration__RelayPassword", _relayPassword)
            .WithEnvironment("DatabaseMigration__MaintenancePassword", _maintenancePassword)
            .WaitFor(database);

        foreach (var host in hosts)
        {
            host.WithEnvironment("Database__ConnectionString", ConnectionString(databaseName, ApplicationRole, _applicationPassword))
                .WaitForCompletion(migrate);
        }
    }

    /// <summary>
    /// Gives <paramref name="worker"/> the relay role's connection (outbox relay, link delivery). The role
    /// bypasses row-level security but is granted only the delivery tables, so only workers get it.
    /// </summary>
    public void AddRelay(string databaseName, IResourceBuilder<ProjectResource> worker) =>
        worker.WithEnvironment("OutboxRelay__ConnectionString", ConnectionString(databaseName, RelayRole, _relayPassword));

    /// <summary>
    /// Gives <paramref name="worker"/> the maintenance role's connection for its cross-organization jobs
    /// (docs/adr/0010). The role bypasses row-level security and holds only the column privileges job
    /// migrations grant it, so only workers get it. No host calls this until the first job exists.
    /// </summary>
    public void AddMaintenance(string databaseName, IResourceBuilder<ProjectResource> worker) =>
        worker.WithEnvironment("MaintenanceJobs__ConnectionString", ConnectionString(databaseName, MaintenanceRole, _maintenancePassword));

    private static IResourceBuilder<ParameterResource> Password(IDistributedApplicationBuilder builder, string name) =>
        builder.AddParameter(name, new GenerateParameterDefault { MinLength = 24, Special = false }, secret: true, persist: true);

    private ReferenceExpression ConnectionString(string databaseName, string role, IResourceBuilder<ParameterResource> password)
    {
        var endpoint = _postgres.Resource.PrimaryEndpoint;
        return ReferenceExpression.Create(
            $"Host={endpoint.Property(EndpointProperty.Host)};Port={endpoint.Property(EndpointProperty.Port)};Database={databaseName};Username={role};Password={password}");
    }
}
