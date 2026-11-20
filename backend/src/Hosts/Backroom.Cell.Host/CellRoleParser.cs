namespace Backroom.Cell.Host;

public static class CellRoleParser
{
    public const string ConfigurationKey = "Cell:Role";

    /// <summary>
    /// Parses the configured role. The role must be set explicitly so a process
    /// never starts in the wrong role by accident.
    /// </summary>
    public static CellRole Parse(string? value)
    {
        if (string.Equals(value, "app", StringComparison.OrdinalIgnoreCase))
        {
            return CellRole.App;
        }

        if (string.Equals(value, "worker", StringComparison.OrdinalIgnoreCase))
        {
            return CellRole.Worker;
        }

        if (string.Equals(value, "migrate", StringComparison.OrdinalIgnoreCase))
        {
            return CellRole.Migrate;
        }

        throw new InvalidOperationException(
            $"Configuration value '{ConfigurationKey}' must be 'app', 'worker' or 'migrate' but was '{value ?? "<missing>"}'.");
    }
}
