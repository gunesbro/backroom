using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Backroom.BuildingBlocks.Presentation.Authentication;
using Backroom.Modules.Identity.Application;

namespace Backroom.Modules.Identity.Presentation;

/// <summary>
/// Checks an OpenID Connect Back-Channel Logout 1.0 logout token (section 2.6) with the same settings that validate access
/// tokens: the realm's signing keys (JWKS, cached and refreshed by the authentication handler's configuration manager),
/// RS256 only, the realm as issuer, one of the people's clients as audience. On top of that it requires what the
/// specification demands of a logout token: <c>iat</c> that is recent (<see cref="MaxAge"/>), a <c>jti</c>, the
/// <c>events</c> claim with <c>http://schemas.openid.net/event/backchannel-logout</c>, no <c>nonce</c>, and a
/// <c>sub</c> (the account; Keycloak always sends it) and a <c>sid</c> (the session; required, because every client sets session-required). An access, ID or refresh token carries no
/// such event, so none of them is accepted here, and a logout token is not accepted as an access token (no <c>typ</c> of
/// <c>Bearer</c>).
/// </summary>
internal sealed partial class LogoutTokenValidator(IOptionsMonitor<JwtBearerOptions> jwt, TimeProvider timeProvider, ILogger<LogoutTokenValidator> logger)
{
    public const string EventName = "http://schemas.openid.net/event/backchannel-logout";

    /// <summary>The longest a token may be (Keycloak's are about 1 KB).</summary>
    public const int MaxTokenLength = 8 * 1024;

    /// <summary>A logout token is used at once; one issued earlier than this (or later than the clock skew allows) is refused.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(2);

    private static readonly string[] s_clients = [KeycloakClients.Web, KeycloakClients.Desktop, KeycloakClients.Operations];

    private static readonly JsonWebTokenHandler s_handler = new();

    [SuppressMessage("Design", "CA1031", Justification = "Whatever goes wrong while checking an untrusted token is a refusal; the reason goes to the log.")]
    public async Task<LogoutTokenResult> ValidateAsync(string? token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(token) || token.Length > MaxTokenLength)
        {
            return LogoutTokenResult.Invalid("missing or oversized");
        }

        try
        {
            var options = jwt.Get(JwtBearerDefaults.AuthenticationScheme);
            if (options.ConfigurationManager is null)
            {
                return LogoutTokenResult.Invalid("no signing keys configured");
            }

            var result = await ValidateSignatureAsync(options, token, cancellationToken);
            if (!result.IsValid || result.SecurityToken is not JsonWebToken parsed)
            {
                return LogoutTokenResult.Invalid("signature, issuer, audience or lifetime: " + result.Exception?.GetType().Name);
            }

            return CheckClaims(parsed);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return LogoutTokenResult.Invalid("unreadable: " + exception.GetType().Name);
        }
    }

    private async Task<TokenValidationResult> ValidateSignatureAsync(JwtBearerOptions options, string token, CancellationToken cancellationToken)
    {
        var result = await s_handler.ValidateTokenAsync(token, await ParametersAsync(options, cancellationToken));
        if (!result.IsValid && result.Exception is SecurityTokenSignatureKeyNotFoundException)
        {
            // Keycloak rotated its keys: fetch them again (the manager limits how often) and try once more.
            options.ConfigurationManager!.RequestRefresh();
            result = await s_handler.ValidateTokenAsync(token, await ParametersAsync(options, cancellationToken));
        }

        return result;
    }

    private async Task<TokenValidationParameters> ParametersAsync(JwtBearerOptions options, CancellationToken cancellationToken)
    {
        var configuration = await options.ConfigurationManager!.GetConfigurationAsync(cancellationToken);
        var parameters = options.TokenValidationParameters.Clone();
        parameters.ValidAudience = null;
        parameters.AudienceValidator = null;
        parameters.ValidAudiences = s_clients;
        parameters.ValidateAudience = true;
        // The same clock as the iat check, so tests can move it.
        parameters.LifetimeValidator = (notBefore, expires, _, _) =>
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            return (notBefore is null || notBefore <= now + AccessTokenAuthenticationExtensions.ClockSkew)
                && (expires is null || expires > now - AccessTokenAuthenticationExtensions.ClockSkew);
        };
        parameters.RequireAudience = true;

        // exp is optional in a logout token; when present it is checked.
#pragma warning disable CA5404 // exp is optional in a logout token (OIDC Back-Channel Logout 1.0, section 2.4); iat freshness is required instead.
        parameters.RequireExpirationTime = false;
#pragma warning restore CA5404
        parameters.ValidateLifetime = true;
        parameters.IssuerSigningKeys = configuration.SigningKeys;
        parameters.IssuerSigningKey = null;
        return parameters;
    }

    private LogoutTokenResult CheckClaims(JsonWebToken token)
    {
        using var payload = JsonDocument.Parse(Base64Url.DecodeFromChars(token.EncodedPayload));
        var root = payload.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return LogoutTokenResult.Invalid("payload is not an object");
        }

        if (root.TryGetProperty("nonce", out _))
        {
            return LogoutTokenResult.Invalid("has a nonce");
        }

        if (!root.TryGetProperty("events", out var events)
            || events.ValueKind != JsonValueKind.Object
            || !events.TryGetProperty(EventName, out var logoutEvent)
            || logoutEvent.ValueKind != JsonValueKind.Object)
        {
            return LogoutTokenResult.Invalid("no back-channel logout event");
        }

        if (root.TryGetProperty("typ", out var type) && type.ValueKind == JsonValueKind.String
            && string.Equals(type.GetString(), KeycloakClaims.AccessTokenType, StringComparison.Ordinal))
        {
            return LogoutTokenResult.Invalid("is an access token");
        }

        if (!root.TryGetProperty("jti", out var jti) || jti.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(jti.GetString()))
        {
            return LogoutTokenResult.Invalid("no jti");
        }

        if (!root.TryGetProperty("iat", out var issuedAtClaim) || !issuedAtClaim.TryGetInt64(out var issuedAtSeconds)
            || issuedAtSeconds is < 0 or > 253_402_300_799)
        {
            return LogoutTokenResult.Invalid("no iat");
        }

        var age = timeProvider.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(issuedAtSeconds);
        if (age > MaxAge || age < -AccessTokenAuthenticationExtensions.ClockSkew)
        {
            return LogoutTokenResult.Invalid("iat is not recent");
        }

        if (!root.TryGetProperty("sub", out var subject) || subject.ValueKind != JsonValueKind.String
            || !Guid.TryParse(subject.GetString(), out var accountId) || accountId == Guid.Empty)
        {
            // Without the account the cells cannot be found; Keycloak always sends the subject of a user session.
            return LogoutTokenResult.Invalid("no account");
        }

        // The sid is required: every client has backchannel.logout.session.required, so Keycloak always sends it, and a token
        // that names no session would end every session of the account (and leave a replay window for an old token).
        if (!root.TryGetProperty("sid", out var sid) || sid.ValueKind != JsonValueKind.String
            || sid.GetString() is not { } sessionId || !SessionIdPattern().IsMatch(sessionId))
        {
            return LogoutTokenResult.Invalid("no or bad sid");
        }

        LogAccepted(logger, accountId);
        return LogoutTokenResult.Valid(accountId, sessionId);
    }

    [GeneratedRegex(@"\A[\x21-\x7E]{1,128}\z", RegexOptions.CultureInvariant)]
    private static partial Regex SessionIdPattern();

    [LoggerMessage(EventId = 20, Level = LogLevel.Debug, Message = "A logout token for account {AccountId} was accepted.")]
    private static partial void LogAccepted(ILogger logger, Guid accountId);
}
