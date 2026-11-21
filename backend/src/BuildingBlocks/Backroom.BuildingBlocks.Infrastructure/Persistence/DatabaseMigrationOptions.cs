namespace Backroom.BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// Settings of a host's <c>migrate</c> run. Every value carries a password, so it comes from user
/// secrets or the environment, never from appsettings files.
/// </summary>
public sealed class DatabaseMigrationOptions
{
    public const string SectionName = "DatabaseMigration";

    /// <summary>Connection string of the migrator role, which owns the module schemas and tables.</summary>
    public string MigratorConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Development only: an administrator connection string to the target database. When set, the
    /// roles are created (or their passwords updated) and given access to the database before
    /// migrating. Elsewhere the roles are provisioned by the deployment.
    /// </summary>
    public string? BootstrapConnectionString { get; set; }

    /// <summary>Development bootstrap: passwords for the migrator, application, relay and maintenance roles.</summary>
    public string? MigratorPassword { get; set; }

    public string? ApplicationPassword { get; set; }

    public string? RelayPassword { get; set; }

    public string? MaintenancePassword { get; set; }
}
