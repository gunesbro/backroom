namespace Backroom.Modules.Presence.Infrastructure;

/// <summary>The cell's Valkey server (configuration section <c>Valkey</c>, shared with Realtime); the connection string is never logged.</summary>
public sealed class PresenceValkeyOptions
{
    public const string SectionName = "Valkey";

    public string ConnectionString { get; set; } = string.Empty;
}
