using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Backroom.Modules.ControlPlaneConnector.Infrastructure.Registration;

/// <summary>
/// The cell's credentials towards Keycloak: its public key as a JWK, and short-lived access tokens
/// obtained with the client-credentials grant and a client assertion signed by the cell's private key
/// (RFC 7523, <c>private_key_jwt</c>). Tokens are cached until shortly before they expire.
/// </summary>
internal sealed class CellCredentials(
    CellKeyStore keyStore,
    IHttpClientFactory httpClientFactory,
    IOptions<ControlPlaneConnectorOptions> options,
    TimeProvider timeProvider) : IDisposable
{
    public const string HttpClientName = "keycloak-token";

    private static readonly TimeSpan s_assertionLifetime = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _accessTokenExpiresAt;

    public string ClientId => "cell-" + options.Value.CellId;

    private string Issuer => options.Value.Authority.TrimEnd('/');

    /// <summary>The public part of the cell's key, as sent to the control plane at registration.</summary>
    public async Task<JsonObject> GetPublicJwkAsync(CancellationToken cancellationToken)
    {
        var parameters = (await keyStore.GetKeyAsync(cancellationToken)).ExportParameters(includePrivateParameters: false);
        return new JsonObject
        {
            ["kty"] = "EC",
            ["crv"] = "P-256",
            ["x"] = Base64UrlEncoder.Encode(parameters.Q.X),
            ["y"] = Base64UrlEncoder.Encode(parameters.Q.Y),
        };
    }

    /// <summary>A valid access token of the cell (audience <c>control-plane</c>).</summary>
    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken) =>
        (await GetAccessTokenWithRefreshTimeAsync(forceRefresh: false, cancellationToken)).Token;

    /// <summary>
    /// A valid access token and the time it leaves the cache (shortly before it expires); from then on
    /// a call returns a new token. <paramref name="forceRefresh"/> skips the cache.
    /// </summary>
    public async Task<(string Token, DateTimeOffset RefreshAt)> GetAccessTokenWithRefreshTimeAsync(
        bool forceRefresh, CancellationToken cancellationToken)
    {
        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            if (!forceRefresh && _accessToken is not null && timeProvider.GetUtcNow() < _accessTokenExpiresAt)
            {
                return (_accessToken, _accessTokenExpiresAt);
            }

            using var form = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = ClientId,
                ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
                ["client_assertion"] = await CreateAssertionAsync(cancellationToken),
            });
            using var http = httpClientFactory.CreateClient(HttpClientName);
            using var response = await http.PostAsync(new Uri(Issuer + "/protocol/openid-connect/token"), form, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"Keycloak refused the cell's credentials with status {(int)response.StatusCode}.");
            }

            var json = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken)
                ?? throw new HttpRequestException("Keycloak returned no token.");
            _accessToken = json["access_token"]!.GetValue<string>();
            var lifetime = TimeSpan.FromSeconds(json["expires_in"]?.GetValue<int>() ?? 60);
            _accessTokenExpiresAt = timeProvider.GetUtcNow() + lifetime - TimeSpan.FromSeconds(Math.Min(60, lifetime.TotalSeconds / 2));
            return (_accessToken, _accessTokenExpiresAt);
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    public void Dispose() => _tokenLock.Dispose();

    private async Task<string> CreateAssertionAsync(CancellationToken cancellationToken)
    {
        var key = await keyStore.GetKeyAsync(cancellationToken);
        var securityKey = new ECDsaSecurityKey(key) { KeyId = await ThumbprintAsync(cancellationToken) };
        var now = timeProvider.GetUtcNow().UtcDateTime;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = ClientId,
            Audience = Issuer,
            IssuedAt = now,
            NotBefore = now,
            Expires = now + s_assertionLifetime,
            Claims = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["sub"] = ClientId,
                ["jti"] = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)),
            },
            SigningCredentials = new SigningCredentials(securityKey, SecurityAlgorithms.EcdsaSha256),
        });
    }

    // RFC 7638, as the control plane computes it; Keycloak finds the key by this id.
    private async Task<string> ThumbprintAsync(CancellationToken cancellationToken)
    {
        var jwk = await GetPublicJwkAsync(cancellationToken);
        var canonical = $$"""{"crv":"P-256","kty":"EC","x":"{{jwk["x"]}}","y":"{{jwk["y"]}}"}""";
        return Base64UrlEncoder.Encode(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical)));
    }
}
