using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Backroom.Modules.Realtime.Infrastructure.Revocation;

/// <summary>
/// After authentication, on every request and every hub connect (the token of a WebSocket arrives in the query string
/// and is authenticated by the same handler): a token whose session (<c>sid</c>) is revoked, or whose account was revoked
/// after the token was issued, gets <c>401</c> with <c>invalid_token</c> (the client signs in again). When the revocation
/// store cannot be read and no recent answer is cached the request gets <c>503</c> with <c>Retry-After</c> (fail closed by default;
/// see <see cref="RevocationChecker"/>), never <c>401</c>, so a Valkey outage does not sign people out. Requests without a
/// valid token pass: the authorization that follows decides about them.
/// </summary>
internal sealed class RevocationMiddleware(RequestDelegate next, RevocationChecker checker, IOptions<RealtimeOptions> options)
{
    public const string RevokedCode = "identity.session_revoked";

    public const string UnavailableCode = "realtime.revocation_check_unavailable";

    /// <summary>How long a client is told to wait before it tries again.</summary>
    private const string RetryAfterSeconds = "2";

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (TokenIdentity.TryRead(context.User, out var identity))
        {
            switch (await checker.CheckAsync(identity.SessionId, identity.AccountId, identity.IssuedAt, context.RequestAborted))
            {
                case RevocationVerdict.Revoked:
                    RealtimeTelemetry.RevokedRequests.Add(1);
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    context.Response.Headers.WWWAuthenticate = "Bearer error=\"invalid_token\", error_description=\"The session was revoked.\"";
                    await Results.Problem(statusCode: StatusCodes.Status401Unauthorized, extensions: Code(RevokedCode)).ExecuteAsync(context);
                    return;
                case RevocationVerdict.Unavailable when options.Value.RevocationFailClosed:
                    RealtimeTelemetry.RevocationChecksUnavailable.Add(1, new KeyValuePair<string, object?>("outcome", "refused"));
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    context.Response.Headers.RetryAfter = RetryAfterSeconds;
                    await Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, extensions: Code(UnavailableCode)).ExecuteAsync(context);
                    return;
                case RevocationVerdict.Unavailable:
                    // The cell is configured to fail open: the request is served and the outage is counted.
                    RealtimeTelemetry.RevocationChecksUnavailable.Add(1, new KeyValuePair<string, object?>("outcome", "allowed"));
                    break;
                default:
                    break;
            }
        }

        await next(context);
    }

    private static Dictionary<string, object?> Code(string code) => new(StringComparer.Ordinal) { ["code"] = code };
}
