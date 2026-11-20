using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Backroom.Modules.CellManagement.Application.Registration;

namespace Backroom.Modules.CellManagement.Infrastructure.Keycloak;

/// <summary>
/// Creates or updates a cell's Keycloak client and scope through the admin REST API, as the
/// <c>control-plane-admin</c> service account. Every step is idempotent, so a failed registration can
/// be retried. Tokens and secrets are never logged; errors carry only the method, path and status.
/// </summary>
internal sealed class KeycloakCellIdentityProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<KeycloakAdminOptions> options,
    TimeProvider timeProvider) : ICellIdentityProvider, IDisposable
{
    public const string HttpClientName = "keycloak-admin";

    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _adminToken;
    private DateTimeOffset _adminTokenExpiresAt;

    private KeycloakAdminOptions Settings => options.Value;

    private string Issuer => Settings.Authority.TrimEnd('/');

    public (string ClientId, string Issuer) CellCredentials(string cellId) => (ClientIdFor(cellId), Issuer);

    public async Task RegisterCellAsync(string cellId, CellPublicKey publicKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        using var http = httpClientFactory.CreateClient(HttpClientName);
        var token = await GetAdminTokenAsync(http, cancellationToken);

        var scopeId = await EnsureCellScopeAsync(http, token, cellId, cancellationToken);
        await UpsertCellClientAsync(http, token, cellId, publicKey, cancellationToken);

        foreach (var clientId in new[] { Settings.WebClientId, Settings.DesktopClientId })
        {
            var client = await FindClientAsync(http, token, clientId, cancellationToken)
                ?? throw new InvalidOperationException($"Keycloak has no client {clientId}.");
            await SendAsync(http, token, HttpMethod.Put, $"clients/{Id(client)}/optional-client-scopes/{scopeId}", body: null, cancellationToken);
        }
    }

    public void Dispose() => _tokenLock.Dispose();

    private static string ClientIdFor(string cellId) => "cell-" + cellId;

    private static string Id(JsonNode node) => node["id"]!.GetValue<string>();

    private async Task<string> EnsureCellScopeAsync(HttpClient http, string token, string cellId, CancellationToken cancellationToken)
    {
        var name = "cell:" + cellId;
        if (await FindScopeAsync(http, token, name, cancellationToken) is { } existing)
        {
            return Id(existing);
        }

        var scope = new JsonObject
        {
            ["name"] = name,
            ["description"] = $"Access token for cell {cellId} (audience cell-{cellId}). Request it alone.",
            ["protocol"] = "openid-connect",
            ["attributes"] = new JsonObject { ["include.in.token.scope"] = "true", ["display.on.consent.screen"] = "false" },
            ["protocolMappers"] = new JsonArray(new JsonObject
            {
                ["name"] = $"cell-{cellId} audience",
                ["protocol"] = "openid-connect",
                ["protocolMapper"] = "oidc-audience-mapper",
                ["config"] = new JsonObject
                {
                    ["included.custom.audience"] = "cell-" + cellId,
                    ["access.token.claim"] = "true",
                    ["id.token.claim"] = "false",
                    ["introspection.token.claim"] = "true",
                },
            }),
        };

        using var response = await SendAsync(http, token, HttpMethod.Post, "client-scopes", scope, cancellationToken, allowConflict: true);
        return (await FindScopeAsync(http, token, name, cancellationToken) is { } created)
            ? Id(created)
            : throw new InvalidOperationException($"Keycloak did not create client scope for cell {cellId}.");
    }

    private async Task UpsertCellClientAsync(HttpClient http, string token, string cellId, CellPublicKey publicKey, CancellationToken cancellationToken)
    {
        var client = new JsonObject
        {
            ["clientId"] = ClientIdFor(cellId),
            ["name"] = $"Cell {cellId}",
            ["enabled"] = true,
            ["protocol"] = "openid-connect",
            ["publicClient"] = false,
            ["clientAuthenticatorType"] = "client-jwt",
            ["serviceAccountsEnabled"] = true,
            ["standardFlowEnabled"] = false,
            ["implicitFlowEnabled"] = false,
            ["directAccessGrantsEnabled"] = false,
            ["frontchannelLogout"] = false,
            ["fullScopeAllowed"] = false,
            ["redirectUris"] = new JsonArray(),
            ["webOrigins"] = new JsonArray(),
            ["defaultClientScopes"] = new JsonArray("basic", "control-plane"),
            ["optionalClientScopes"] = new JsonArray(),
            ["attributes"] = new JsonObject
            {
                ["use.jwks.url"] = "false",
                ["use.jwks.string"] = "true",
                ["jwks.string"] = publicKey.ToJwks(),
                ["token.endpoint.auth.signing.alg"] = "ES256",
            },
        };

        if (await FindClientAsync(http, token, ClientIdFor(cellId), cancellationToken) is { } existing)
        {
            client["id"] = Id(existing);
            using var updated = await SendAsync(http, token, HttpMethod.Put, $"clients/{Id(existing)}", client, cancellationToken);
            return;
        }

        using var created = await SendAsync(http, token, HttpMethod.Post, "clients", client, cancellationToken, allowConflict: true);
        if (created.StatusCode == HttpStatusCode.Conflict)
        {
            // Created by a concurrent attempt; update it with this key.
            await UpsertCellClientAsync(http, token, cellId, publicKey, cancellationToken);
        }
    }

    private async Task<JsonNode?> FindClientAsync(HttpClient http, string token, string clientId, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(http, token, HttpMethod.Get, $"clients?clientId={Uri.EscapeDataString(clientId)}&first=0&max=2", body: null, cancellationToken);
        var clients = await response.Content.ReadFromJsonAsync<JsonArray>(cancellationToken);
        return clients?.SingleOrDefault(node => node?["clientId"]?.GetValue<string>() == clientId);
    }

    private async Task<JsonNode?> FindScopeAsync(HttpClient http, string token, string name, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(http, token, HttpMethod.Get, "client-scopes", body: null, cancellationToken);
        var scopes = await response.Content.ReadFromJsonAsync<JsonArray>(cancellationToken);
        return scopes?.SingleOrDefault(node => node?["name"]?.GetValue<string>() == name);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpClient http, string token, HttpMethod method, string path, JsonNode? body, CancellationToken cancellationToken, bool allowConflict = false)
    {
        using var request = new HttpRequestMessage(method, new Uri(AdminBase(), path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode && !(allowConflict && response.StatusCode == HttpStatusCode.Conflict))
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new HttpRequestException($"Keycloak admin request {method} {path.Split('?')[0]} failed with status {status}.");
        }

        return response;
    }

    // https://host/realms/{realm} → https://host/admin/realms/{realm}/
    private Uri AdminBase()
    {
        var issuer = new Uri(Issuer + "/");
        var path = issuer.AbsolutePath;
        var index = path.LastIndexOf("/realms/", StringComparison.Ordinal);
        if (index < 0)
        {
            throw new InvalidOperationException("The Keycloak authority must end with /realms/{realm}.");
        }

        return new Uri(issuer, path[..index] + "/admin" + path[index..]);
    }

    private async Task<string> GetAdminTokenAsync(HttpClient http, CancellationToken cancellationToken)
    {
        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            if (_adminToken is not null && timeProvider.GetUtcNow() < _adminTokenExpiresAt)
            {
                return _adminToken;
            }

            using var form = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = Settings.AdminClientId,
                ["client_secret"] = Settings.AdminClientSecret,
            });
            using var response = await http.PostAsync(new Uri(Issuer + "/protocol/openid-connect/token"), form, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"Keycloak refused the control plane's admin credentials with status {(int)response.StatusCode}.");
            }

            var json = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken)
                ?? throw new HttpRequestException("Keycloak returned no token.");
            _adminToken = json["access_token"]!.GetValue<string>();
            var lifetime = TimeSpan.FromSeconds(json["expires_in"]?.GetValue<int>() ?? 60);
            _adminTokenExpiresAt = timeProvider.GetUtcNow() + lifetime - TimeSpan.FromSeconds(Math.Min(30, lifetime.TotalSeconds / 2));
            return _adminToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }
}
