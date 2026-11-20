using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Backroom.BuildingBlocks.Application.Accounts;
using Backroom.BuildingBlocks.Presentation.Errors;
using Backroom.BuildingBlocks.Presentation.RateLimiting;
using Backroom.Modules.Directory.Application;

namespace Backroom.Modules.Directory.Presentation;

/// <summary>
/// Applies <see cref="InvitationAttemptLimits"/> to <c>resolve</c> and <c>accept</c>, and records every refused code
/// (invalid or not the account's) as a failure and a security log event with the account id only: never the code,
/// the address or the client's network.
/// </summary>
internal sealed partial class InvitationAttemptFilter(
    InvitationAttemptLimits limits, ICurrentAccount account, ILogger<InvitationAttemptFilter> logger) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var httpContext = context.HttpContext;

        // Answers carry what the page shows: never cached on the way.
        httpContext.Response.Headers.CacheControl = "no-store";

        var network = ClientNetwork.PartitionKey(httpContext);
        if (limits.TryAcquire(account.AccountId.ToString("N"), network) is { } retryAfter)
        {
            LogRateLimited(logger, account.AccountId);
            return DirectoryErrors.InvitationRateLimited(retryAfter).ToProblem();
        }

        var result = await next(context);
        if (result is IStatusCodeHttpResult { StatusCode: StatusCodes.Status404NotFound or StatusCodes.Status403Forbidden } refused)
        {
            LogRefused(logger, account.AccountId, refused.StatusCode ?? 0);

            // Over the failure limit only failures are refused: a valid code still works from the same network, and a
            // refused one does not tell which kind of failure it was.
            if (limits.RecordFailure(network) is { } failuresRetryAfter)
            {
                LogFailureLimitReached(logger, account.AccountId);
                return DirectoryErrors.InvitationRateLimited(failuresRetryAfter).ToProblem();
            }
        }

        return result;
    }

    [LoggerMessage(EventId = 40, Level = LogLevel.Warning, Message = "Security: an invitation code was refused for account {AccountId} with status {StatusCode}.")]
    private static partial void LogRefused(ILogger logger, Guid accountId, int statusCode);

    [LoggerMessage(EventId = 41, Level = LogLevel.Warning, Message = "Security: invitation attempts of account {AccountId} were rate limited.")]
    private static partial void LogRateLimited(ILogger logger, Guid accountId);

    [LoggerMessage(EventId = 42, Level = LogLevel.Warning, Message = "Security: the network of account {AccountId} reached the limit of failed invitation attempts.")]
    private static partial void LogFailureLimitReached(ILogger logger, Guid accountId);
}
