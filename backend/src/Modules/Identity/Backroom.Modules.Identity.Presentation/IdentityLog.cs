using Microsoft.Extensions.Logging;

namespace Backroom.Modules.Identity.Presentation;

internal static partial class IdentityLog
{
    [LoggerMessage(EventId = 21, Level = LogLevel.Warning, Message = "Security: a back-channel logout was refused: {Reason}.")]
    public static partial void LogoutRefused(ILogger logger, string reason);

    [LoggerMessage(EventId = 22, Level = LogLevel.Error, Message = "Security: recording the revocation of a session of account {AccountId} failed ({ExceptionType}); Keycloak does not retry, so the session stays usable until its access token expires.")]
    public static partial void LogoutFailed(ILogger logger, Guid accountId, string exceptionType);
}
