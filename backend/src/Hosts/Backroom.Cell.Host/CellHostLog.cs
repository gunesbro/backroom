namespace Backroom.Cell.Host;

/// <summary>
/// Source-generated log messages: no formatting or boxing work when the level is disabled.
/// </summary>
internal static partial class CellHostLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Cell host starting in {CellRole} role")]
    public static partial void Starting(ILogger logger, CellRole cellRole);
}
