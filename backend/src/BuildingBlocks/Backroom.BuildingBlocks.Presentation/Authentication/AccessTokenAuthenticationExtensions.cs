using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Backroom.BuildingBlocks.Application.Accounts;

namespace Backroom.BuildingBlocks.Presentation.Authentication;

public static class AccessTokenAuthenticationExtensions
{
    /// <summary>Tolerated clock difference with Keycloak; small, because access tokens live five minutes.</summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Validates Keycloak access tokens locally against the realm's signing keys (JWKS, cached and
    /// refreshed by the handler). A token is accepted only if it is signed with RS256 by the realm,
    /// unexpired, an access token, issued to one of <paramref name="allowedClients"/> (<c>azp</c>) for
    /// a signed-in person (with a session and an account id), and its only audience is
    /// <paramref name="audience"/>: a token that names several targets is accepted by none, so a token
    /// given to one cell cannot be replayed against another service. Every endpoint requires such a
    /// token unless it allows anonymous access explicitly. Outside Development the authority must use
    /// HTTPS and failure details are not sent to clients.
    /// </summary>
    public static IHostApplicationBuilder AddAccessTokenAuthentication(
        this IHostApplicationBuilder builder,
        string audience,
        IReadOnlyCollection<string> allowedClients)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(audience);
        ArgumentNullException.ThrowIfNull(allowedClients);
        if (allowedClients.Count == 0)
        {
            throw new ArgumentException("At least one client must be allowed.", nameof(allowedClients));
        }

        var development = builder.Environment.IsDevelopment();
        var clients = allowedClients.ToHashSet(StringComparer.Ordinal);

        builder.Services.AddOptions<AccessTokenOptions>()
            .BindConfiguration(AccessTokenOptions.SectionName)
            .Validate(
                options => IsValidAuthority(options.Authority, development),
                development
                    ? "Authentication:Authority must be an absolute http or https URL of the Keycloak realm."
                    : "Authentication:Authority must be an absolute https URL of the Keycloak realm.")
            .ValidateOnStart();

        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<AccessTokenOptions>>((jwt, tokens) =>
                Configure(jwt, tokens.Value.Authority, audience, context => ValidatePersonClaimsAsync(context, clients), includeErrorDetails: development));

        // The requirement is its own handler, so it needs no registration.
        builder.Services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(AuthorizationPolicies.Operations, policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(new OperationsRequirement()));

        builder.Services.AddHttpContextAccessor();
        builder.Services.TryAddScoped<ICurrentAccount, HttpCurrentAccount>();
        return builder;
    }

    /// <summary>
    /// Control plane only, after <see cref="AddAccessTokenAuthentication"/>: the scheme and policy
    /// <see cref="AuthorizationPolicies.Cell"/> for cells. A cell token is validated like a person's
    /// (same realm, RS256, audience <c>control-plane</c> only) but must be issued to a cell's client
    /// (<c>azp = cell-{id}</c>) with client credentials, so it has no session. The scheme is used only by
    /// endpoints that require the policy; person endpoints never accept cell tokens, and the cell
    /// endpoints never accept person tokens.
    /// </summary>
    public static IHostApplicationBuilder AddCellTokenAuthentication(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var development = builder.Environment.IsDevelopment();

        builder.Services.AddAuthentication().AddJwtBearer(CellScheme);
        builder.Services.AddOptions<JwtBearerOptions>(CellScheme)
            .Configure<IOptions<AccessTokenOptions>>((jwt, tokens) =>
                Configure(jwt, tokens.Value.Authority, Audiences.ControlPlane, ValidateCellClaimsAsync, includeErrorDetails: development));

        builder.Services.AddAuthorizationBuilder()
            .AddPolicy(AuthorizationPolicies.Cell, new AuthorizationPolicyBuilder(CellScheme).RequireAuthenticatedUser().Build());
        return builder;
    }

    /// <summary>The authentication scheme of cell tokens.</summary>
    public const string CellScheme = "Cell";

    private static bool IsValidAuthority(string authority, bool allowHttp) =>
        Uri.TryCreate(authority, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || (allowHttp && uri.Scheme == Uri.UriSchemeHttp))
        && string.IsNullOrEmpty(uri.Query)
        && string.IsNullOrEmpty(uri.Fragment);

    private static void Configure(
        JwtBearerOptions jwt, string authority, string audience, Func<TokenValidatedContext, Task> validateClaims, bool includeErrorDetails)
    {
        var issuer = authority.TrimEnd('/');
        jwt.Authority = issuer;
        jwt.Audience = audience;

        // Plain HTTP passes options validation only in Development (a local Keycloak without a certificate).
        jwt.RequireHttpsMetadata = issuer.StartsWith("https:", StringComparison.OrdinalIgnoreCase);
        jwt.IncludeErrorDetails = includeErrorDetails;

        // Keep Keycloak's claim names (sub, sid, typ, azp, realm_access, amr) instead of legacy mappings.
        jwt.MapInboundClaims = false;
        jwt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = issuer,
            ValidAudience = audience,
            AudienceValidator = (audiences, _, _) => IsOnlyAudience(audiences, audience),
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            RequireAudience = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ClockSkew = ClockSkew,
            NameClaimType = KeycloakClaims.Subject,
        };
        jwt.Events = new JwtBearerEvents { OnTokenValidated = validateClaims };
    }

    private static bool IsOnlyAudience(IEnumerable<string> audiences, string audience)
    {
        using var enumerator = audiences.GetEnumerator();
        return enumerator.MoveNext()
            && string.Equals(enumerator.Current, audience, StringComparison.Ordinal)
            && !enumerator.MoveNext();
    }

    // ID, refresh and logout tokens are signed by the same keys; only access tokens are accepted.
    // Service-account tokens (client credentials) have no session and come from other clients.
    private static Task ValidatePersonClaimsAsync(TokenValidatedContext context, HashSet<string> allowedClients)
    {
        var principal = context.Principal;
        if (!string.Equals(principal?.FindFirst(KeycloakClaims.TokenType)?.Value, KeycloakClaims.AccessTokenType, StringComparison.Ordinal))
        {
            context.Fail("The token is not an access token.");
        }
        else if (principal!.FindFirst(KeycloakClaims.AuthorizedParty)?.Value is not { } client || !allowedClients.Contains(client))
        {
            context.Fail("The token was issued to a client this service does not accept.");
        }
        else if (string.IsNullOrEmpty(principal.FindFirst(KeycloakClaims.SessionId)?.Value))
        {
            context.Fail("The token does not belong to a signed-in session.");
        }
        else if (!Guid.TryParse(principal.FindFirst(KeycloakClaims.Subject)?.Value, out _))
        {
            context.Fail("The token does not identify an account.");
        }

        return Task.CompletedTask;
    }

    // A cell's service-account token: an access token of a cell client, without a person's session.
    private static Task ValidateCellClaimsAsync(TokenValidatedContext context)
    {
        var principal = context.Principal;
        if (!string.Equals(principal?.FindFirst(KeycloakClaims.TokenType)?.Value, KeycloakClaims.AccessTokenType, StringComparison.Ordinal))
        {
            context.Fail("The token is not an access token.");
        }
        else if (!CellClients.TryGetCellId(principal!, out _))
        {
            context.Fail("The token was not issued to a cell.");
        }
        else if (principal!.FindFirst(KeycloakClaims.SessionId) is not null)
        {
            context.Fail("A cell token has no user session.");
        }

        return Task.CompletedTask;
    }
}
