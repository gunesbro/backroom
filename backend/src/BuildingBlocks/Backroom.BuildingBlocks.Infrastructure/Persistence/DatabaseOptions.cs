namespace Backroom.BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// Connection settings for the application's database role. The connection string carries a password,
/// so it comes from user secrets or the environment (Aspire), never from <c>appsettings*.json</c>.
/// </summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    public const int MaxPoolSizeLimit = 200;

    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Connections this process may hold. Set explicitly instead of Npgsql's default of 100, because
    /// every request holds one connection for its whole unit of work.
    /// </summary>
    public int MaxPoolSize { get; set; } = 30;
}
