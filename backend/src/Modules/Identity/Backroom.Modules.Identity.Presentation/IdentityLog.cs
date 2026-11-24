using Microsoft.Extensions.Logging;

namespace Backroom.Modules.Identity.Presentation;

internal static partial class IdentityLog
{
    [LoggerMessage(EventId = 21, Level = LogLevel.Warning, Message = "Security: a back-channel logout was refused: {Reason}.")]
    public static partial void LogoutRefused(ILogger logger, string reason);
}
