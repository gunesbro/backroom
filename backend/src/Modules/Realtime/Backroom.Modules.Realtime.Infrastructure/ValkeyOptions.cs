namespace Backroom.Modules.Realtime.Infrastructure;

/// <summary>The shared Valkey server (configuration section <c>Valkey</c>); the connection string carries the password and is never logged.</summary>
public sealed class ValkeyOptions
{
    public const string SectionName = "Valkey";

    public string ConnectionString { get; set; } = string.Empty;
}
