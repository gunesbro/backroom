namespace Backroom.BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// Database role names, the same in every deployment (the passwords differ and come from secrets).
/// Migrations grant table privileges to them by name.
/// </summary>
public static class DatabaseRoles
{
    /// <summary>Owns schemas and tables; runs migrations only.</summary>
    public const string Migrator = "backroom_migrator";

    /// <summary>What services connect as: data access only, subject to row-level security.</summary>
    public const string Application = "backroom_app";

    /// <summary>
    /// The outbox relay: BYPASSRLS, because it handles every organization, but privileges on outbox
    /// tables only.
    /// </summary>
    public const string Relay = "backroom_relay";

    /// <summary>
    /// Cross-organization maintenance jobs (docs/adr/0010): BYPASSRLS, because a job handles every
    /// organization in one bounded batch, but no table privileges by default. Each job's module grants
    /// it the columns that job needs in its migration (<c>MaintenanceGrants</c>).
    /// </summary>
    public const string Maintenance = "backroom_maintenance";
}
