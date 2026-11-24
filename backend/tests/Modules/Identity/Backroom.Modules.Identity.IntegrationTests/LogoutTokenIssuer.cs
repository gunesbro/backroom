using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Backroom.Modules.Identity.IntegrationTests;

/// <summary>Signs logout tokens shaped like Keycloak's with a key generated for the test run, which the test host trusts as the realm's key.</summary>
internal sealed class LogoutTokenIssuer : IDisposable
{
    public const string Issuer = "https://id.example.test/realms/backroom";

    public const string EventName = "http://schemas.openid.net/event/backchannel-logout";

    private readonly RSA _rsa = RSA.Create(2048);
    private readonly RSA _otherRsa = RSA.Create(2048);

    public LogoutTokenIssuer()
    {
        TrustedKey = new RsaSecurityKey(_rsa) { KeyId = "test-key" };
        UntrustedKey = new RsaSecurityKey(_otherRsa) { KeyId = "test-key" };
    }

    /// <summary>The key the control plane trusts (as if published in the realm's JWKS).</summary>
    public RsaSecurityKey TrustedKey { get; }

    /// <summary>A key it does not trust, with the same key id.</summary>
    public RsaSecurityKey UntrustedKey { get; }

    /// <summary>
    /// A valid logout token for <paramref name="accountId"/> issued at <paramref name="now"/>; <paramref name="change"/> edits the
    /// payload to break one rule.
    /// </summary>
    public string Create(
        DateTimeOffset now,
        Guid accountId,
        string? sessionId = null,
        Action<JsonObject>? change = null,
        SecurityKey? key = null,
        string algorithm = SecurityAlgorithms.RsaSha256)
    {
        var payload = new JsonObject
        {
            ["iss"] = Issuer,
            ["aud"] = "web",
            ["sub"] = accountId.ToString("D"),
            ["iat"] = now.ToUnixTimeSeconds(),
            ["jti"] = Guid.NewGuid().ToString("D"),
            ["typ"] = "Logout",
            ["events"] = new JsonObject { [EventName] = new JsonObject() },
        };
        payload["sid"] = sessionId ?? Guid.NewGuid().ToString("D");

        change?.Invoke(payload);
        var credentials = key switch
        {
            SymmetricSecurityKey symmetric => new SigningCredentials(symmetric, algorithm),
            _ => new SigningCredentials(key ?? TrustedKey, algorithm),
        };
        return new JsonWebTokenHandler().CreateToken(payload.ToJsonString(), credentials);
    }

    /// <summary>
    /// An access token of the web client for the control plane, shaped like Keycloak's, valid for five minutes of real time (the JWT
    /// handler checks the lifetime against the system clock, not the test's fake one).
    /// </summary>
    public string CreateAccessToken(Guid accountId)
    {
        var now = DateTimeOffset.UtcNow;
        var payload = new JsonObject
        {
            ["iss"] = Issuer,
            ["aud"] = "control-plane",
            ["azp"] = "web",
            ["typ"] = "Bearer",
            ["sub"] = accountId.ToString("D"),
            ["sid"] = Guid.NewGuid().ToString("D"),
            ["iat"] = now.ToUnixTimeSeconds(),
            ["exp"] = now.AddMinutes(5).ToUnixTimeSeconds(),
        };
        return new JsonWebTokenHandler().CreateToken(payload.ToJsonString(), new SigningCredentials(TrustedKey, SecurityAlgorithms.RsaSha256));
    }

    /// <summary>An unsigned token (<c>alg: none</c>) with the payload of a valid one.</summary>
    public string CreateUnsigned(DateTimeOffset now, Guid accountId)
    {
        var valid = Create(now, accountId);
        var payload = valid.Split('.')[1];
        var header = Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"alg":"none","typ":"JWT"}""")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{header}.{payload}.";
    }

    public void Dispose()
    {
        _rsa.Dispose();
        _otherRsa.Dispose();
    }
}
