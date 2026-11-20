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
}
