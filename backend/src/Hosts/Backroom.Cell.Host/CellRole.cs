namespace Backroom.Cell.Host;

/// <summary>
/// The role a cell process runs in. The same binary serves every role.
/// </summary>
public enum CellRole
{
    /// <summary>REST and SignalR endpoints; scaled horizontally.</summary>
    App,

    /// <summary>Queue consumers, scheduled jobs, email, calendar sync, recording processing.</summary>
    Worker,

    /// <summary>Applies the modules' database migrations, then exits; runs before the other roles start.</summary>
    Migrate,
}
