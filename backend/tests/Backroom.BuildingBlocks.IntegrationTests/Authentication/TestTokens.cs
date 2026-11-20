using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Backroom.BuildingBlocks.Presentation.Authentication;

namespace Backroom.BuildingBlocks.IntegrationTests.Authentication;

/// <summary>Creates Keycloak-shaped access tokens signed by a key generated for the test run.</summary>
public sealed class TestTokens : IDisposable
{
    public const string Issuer = "https://id.example.test/realms/backroom";

    private readonly RSA _rsa = RSA.Create(2048);
    private readonly RSA _otherRsa = RSA.Create(2048);

    public TestTokens()
    {
        SigningKey = new RsaSecurityKey(_rsa) { KeyId = "test-key" };
        OtherKey = new RsaSecurityKey(_otherRsa) { KeyId = "test-key" };
    }

    /// <summary>The key the services trust (as if published in the realm's JWKS).</summary>
    public RsaSecurityKey SigningKey { get; }

    /// <summary>A key the services do not trust, with the same key id.</summary>
    public RsaSecurityKey OtherKey { get; }

    public string Create(
        string audience,
        Guid? accountId = null,
        string issuer = Issuer,
        string? tokenType = KeycloakClaims.AccessTokenType,
        string? subject = null,
        DateTime? expires = null,
        IReadOnlyCollection<string>? realmRoles = null,
        IReadOnlyCollection<string>? authenticationMethods = null,
        SecurityKey? key = null,
        string algorithm = SecurityAlgorithms.RsaSha256,
        string? client = KeycloakClients.Web,
        bool withSession = true,
        IReadOnlyCollection<string>? otherAudiences = null,
        DateTime? notBefore = null)
    {
        var now = DateTime.UtcNow;
        var claims = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [KeycloakClaims.Subject] = subject ?? (accountId ?? Guid.NewGuid()).ToString(),
        };
        if (withSession)
        {
            claims[KeycloakClaims.SessionId] = "session-1";
        }

        if (client is not null)
        {
            claims[KeycloakClaims.AuthorizedParty] = client;
        }

        if (otherAudiences is not null)
        {
            claims["aud"] = new[] { audience }.Concat(otherAudiences).ToArray();
        }
        if (tokenType is not null)
        {
            claims[KeycloakClaims.TokenType] = tokenType;
        }

        if (realmRoles is not null)
        {
            claims[KeycloakClaims.RealmAccess] = new Dictionary<string, object> { ["roles"] = realmRoles.ToArray() };
        }

        if (authenticationMethods is not null)
        {
            claims[KeycloakClaims.AuthenticationMethods] = authenticationMethods.ToArray();
        }

        var expiry = expires ?? now.AddMinutes(5);
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = otherAudiences is null ? audience : null,
            IssuedAt = notBefore ?? expiry.AddMinutes(-5),
            NotBefore = notBefore ?? expiry.AddMinutes(-5),
            Expires = expiry,
            Claims = claims,
            SigningCredentials = new SigningCredentials(key ?? SigningKey, algorithm),
        });
    }

    /// <summary>An unsigned token (<c>alg: none</c>) with otherwise valid claims.</summary>
    public static string CreateUnsigned(string audience)
    {
        var header = Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes("""{"alg":"none","typ":"JWT"}"""));
        var expires = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds();
        var payload = Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(
            $$"""{"iss":"{{Issuer}}","aud":"{{audience}}","sub":"{{Guid.NewGuid()}}","typ":"Bearer","exp":{{expires}}}"""));
        return $"{header}.{payload}.";
    }

    public void Dispose()
    {
        _rsa.Dispose();
        _otherRsa.Dispose();
    }
}
