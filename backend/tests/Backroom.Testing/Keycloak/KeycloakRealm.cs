using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Testcontainers.Keycloak;
using Xunit;

namespace Backroom.Testing.Keycloak;

/// <summary>
/// A Keycloak container with the development realm (backend/src/Aspire/Backroom.AppHost/keycloak-dev)
/// imported, shared by the tests of a class. Test projects copy the realm file to
/// <c>keycloak/backroom-realm.json</c> in their output. The realm and client settings are captured right after
/// the import, before any test changes them.
/// </summary>
public sealed class KeycloakRealm : IAsyncLifetime
{
    public const string Name = "backroom";

    /// <summary>The same version as the AppHost's Keycloak container.</summary>
    public const string Version = "26.8.1";

    private const string Image = "quay.io/keycloak/keycloak:" + Version;

    /// <summary>The same as the AppHost's Keycloak (docs/adr/0009-keycloak-features.md).</summary>
    public const string DisabledFeatures = "account,account-api,impersonation";

    private readonly string _userPassword = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));

    /// <summary>The secret of the control plane's admin client in this realm.</summary>
    public string ControlPlaneAdminSecret { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
    private readonly HttpClient _http = new();
    private readonly KeycloakContainer _container;
    private byte[]? _themesJar;

    public KeycloakRealm()
    {
        _container = new KeycloakBuilder(Image)
            .WithRealm(Path.Combine(AppContext.BaseDirectory, "keycloak", "backroom-realm.json"))
            .WithEnvironment("BACKROOM_DEV_USER_PASSWORD", _userPassword)
            .WithEnvironment("BACKROOM_CONTROL_PLANE_ADMIN_SECRET", ControlPlaneAdminSecret)
            .WithEnvironment("BACKROOM_SMTP_HOST", "localhost")
            .WithEnvironment("BACKROOM_SMTP_PORT", "1025")
            .WithEnvironment("KC_FEATURES_DISABLED", DisabledFeatures)
            .Build();
    }

    public string Url => $"{BaseAddress}/realms/{Name}";

    /// <summary>The realm as imported.</summary>
    public JsonNode ImportedRealm { get; private set; } = null!;

    /// <summary>The web, desktop and ops clients as imported, by client id.</summary>
    public IReadOnlyDictionary<string, JsonNode> ImportedClients { get; private set; } = null!;

    private string BaseAddress => _container.GetBaseAddress().TrimEnd('/');

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        var admin = await AdminTokenAsync();
        ImportedRealm = await GetJsonAsync(admin, $"/admin/realms/{Name}");
        ImportedClients = new Dictionary<string, JsonNode>(StringComparer.Ordinal)
        {
            ["web"] = await GetClientAsync(admin, "web"),
            ["desktop"] = await GetClientAsync(admin, "desktop"),
            ["ops"] = await GetClientAsync(admin, "ops"),
        };
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _container.DisposeAsync();
    }

    public async Task<string> UserIdAsync(string username) =>
        (await GetJsonAsync(await AdminTokenAsync(), $"/admin/realms/{Name}/users?username={Uri.EscapeDataString(username)}&exact=true"))
            .AsArray().Single()!["id"]!.GetValue<string>();

    /// <summary>Signs in with a password; see <see cref="PasswordGrantAsync"/>.</summary>
    public async Task<(string AccessToken, string IdToken)> SignInAsync(string username, string scope)
    {
        var (status, json) = await PasswordGrantAsync(username, scope);
        if (status != HttpStatusCode.OK)
        {
            throw new InvalidOperationException($"Password sign-in for {username} failed with {(int)status}: {json["error"]}.");
        }

        return (json["access_token"]!.GetValue<string>(), json["id_token"]!.GetValue<string>());
    }

    /// <summary>
    /// The token endpoint's answer to a password sign-in with the web client, success or not. The realm allows
    /// password sign-in for no client, so it is enabled on the web client of this throwaway container only.
    /// </summary>
    public async Task<(HttpStatusCode Status, JsonNode Body)> PasswordGrantAsync(string username, string scope)
    {
        var admin = await AdminTokenAsync();
        var client = await GetClientAsync(admin, "web");
        client["directAccessGrantsEnabled"] = true;
        using (var update = new HttpRequestMessage(HttpMethod.Put, AdminUri($"/admin/realms/{Name}/clients/{client["id"]!.GetValue<string>()}")))
        {
            update.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);
            update.Content = JsonContent.Create(client);
            using var updated = await _http.SendAsync(update);
            updated.EnsureSuccessStatusCode();
        }

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = "password",
            ["client_id"] = "web",
            ["username"] = username,
            ["password"] = _userPassword,
            ["scope"] = scope,
        });
        using var response = await _http.PostAsync(new Uri($"{Url}/protocol/openid-connect/token"), form);
        var body = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, string.IsNullOrWhiteSpace(body) ? new JsonObject() : JsonNode.Parse(body)!);
    }

    /// <summary>The token endpoint's answer to a refresh with the web client, success or not.</summary>
    public async Task<(HttpStatusCode Status, JsonNode Body)> RefreshAsync(string refreshToken, string? scope)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = "web",
            ["refresh_token"] = refreshToken,
        };
        if (scope is not null)
        {
            fields["scope"] = scope;
        }

        using var form = new FormUrlEncodedContent(fields);
        using var response = await _http.PostAsync(new Uri($"{Url}/protocol/openid-connect/token"), form);
        var body = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, string.IsNullOrWhiteSpace(body) ? new JsonObject() : JsonNode.Parse(body)!);
    }

    /// <summary>
    /// Creates a verified user with the shared test password through the admin API, which, like sign-up, gives the
    /// realm's default roles (users imported with the realm do not get them).
    /// </summary>
    public async Task CreateUserAsync(string username)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, AdminUri($"/admin/realms/{Name}/users"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AdminTokenAsync());
        request.Content = JsonContent.Create(new JsonObject
        {
            ["username"] = username,
            ["email"] = username,
            ["emailVerified"] = true,
            ["firstName"] = "New",
            ["lastName"] = "Test",
            ["enabled"] = true,
            ["credentials"] = new JsonArray(new JsonObject { ["type"] = "password", ["value"] = _userPassword, ["temporary"] = false }),
        });
        using var response = await _http.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>The names of the realm roles a user has, directly or through the default role.</summary>
    public async Task<IReadOnlyList<string>> EffectiveRealmRolesAsync(string username) =>
        (await GetJsonAsync(await AdminTokenAsync(), $"/admin/realms/{Name}/users/{await UserIdAsync(username)}/role-mappings/realm/composite"))
            .AsArray().Select(role => role!["name"]!.GetValue<string>()).ToList();

    /// <summary>The client scopes the realm adds as optional to every new client.</summary>
    public async Task<IReadOnlyList<string>> DefaultOptionalClientScopesAsync() =>
        (await GetJsonAsync(await AdminTokenAsync(), $"/admin/realms/{Name}/default-optional-client-scopes"))
            .AsArray().Select(scope => scope!["name"]!.GetValue<string>()).ToList();

    /// <summary>The default and optional client scopes of every client in the realm, built-in ones included.</summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> ClientScopesAsync()
    {
        var admin = await AdminTokenAsync();
        var scopes = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var client in (await GetJsonAsync(admin, $"/admin/realms/{Name}/clients")).AsArray())
        {
            var id = client!["id"]!.GetValue<string>();
            var names = new List<string>();
            foreach (var kind in new[] { "default-client-scopes", "optional-client-scopes" })
            {
                names.AddRange((await GetJsonAsync(admin, $"/admin/realms/{Name}/clients/{id}/{kind}"))
                    .AsArray().Select(scope => scope!["name"]!.GetValue<string>()));
            }

            scopes[client["clientId"]!.GetValue<string>()] = names;
        }

        return scopes;
    }

    /// <summary>
    /// Adds a client scope to a client's optional scopes until the returned value is disposed, so a test can check
    /// what a later layer (such as the user's roles) does when the client layer would allow it.
    /// </summary>
    public async Task<IAsyncDisposable> AddOptionalClientScopeAsync(string clientId, string scope)
    {
        var admin = await AdminTokenAsync();
        var client = (await GetClientAsync(admin, clientId))["id"]!.GetValue<string>();
        var scopeId = (await GetJsonAsync(admin, $"/admin/realms/{Name}/client-scopes")).AsArray()
            .Single(candidate => candidate!["name"]!.GetValue<string>() == scope)!["id"]!.GetValue<string>();
        var path = $"/admin/realms/{Name}/clients/{client}/optional-client-scopes/{scopeId}";
        await SendAdminAsync(admin, HttpMethod.Put, path);
        return new Removal(async () => await SendAdminAsync(await AdminTokenAsync(), HttpMethod.Delete, path));
    }

    /// <summary>
    /// Creates a cell-like client scope (<c>cell:{id}</c> with the audience <c>cell-{id}</c>) offered by the web
    /// client, as cell registration does, for a new random id; returns the id.
    /// </summary>
    public async Task<string> CreateCellScopeAsync()
    {
        var id = "t" + Guid.NewGuid().ToString("N")[..12];
        var admin = await AdminTokenAsync();
        using (var request = new HttpRequestMessage(HttpMethod.Post, AdminUri($"/admin/realms/{Name}/client-scopes")))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);
            request.Content = JsonContent.Create(new JsonObject
            {
                ["name"] = "cell:" + id,
                ["protocol"] = "openid-connect",
                ["attributes"] = new JsonObject { ["include.in.token.scope"] = "true", ["display.on.consent.screen"] = "false" },
                ["protocolMappers"] = new JsonArray(new JsonObject
                {
                    ["name"] = "audience",
                    ["protocol"] = "openid-connect",
                    ["protocolMapper"] = "oidc-audience-mapper",
                    ["config"] = new JsonObject { ["included.custom.audience"] = "cell-" + id, ["access.token.claim"] = "true" },
                }),
            });
            using var response = await _http.SendAsync(request);
            response.EnsureSuccessStatusCode();
        }

        _ = await AddOptionalClientScopeAsync("web", "cell:" + id);
        return id;
    }

    /// <summary>
    /// The seconds between resends of the verification email: the realm's setting for the verify-email action, or else this
    /// Keycloak's default, as its admin API describes it.
    /// </summary>
    public async Task<int> VerificationEmailCooldownSecondsAsync()
    {
        const string Setting = "emailResendCooldownSeconds";
        const string Action = "authentication/required-actions/VERIFY_EMAIL";
        var admin = await AdminTokenAsync();
        var configured = (await GetJsonAsync(admin, $"/admin/realms/{Name}/{Action}"))["config"]?[Setting]?.GetValue<string>();
        var value = configured ?? (await GetJsonAsync(admin, $"/admin/realms/{Name}/{Action}/config-description"))["properties"]!
            .AsArray().Single(property => property!["name"]!.GetValue<string>() == Setting)!["defaultValue"]!.GetValue<string>();
        return int.Parse(value, CultureInfo.InvariantCulture);
    }

    /// <summary>The server's feature list (name and whether it is enabled), as the admin API reports it.</summary>
    public async Task<JsonNode> ServerFeaturesAsync() =>
        (await GetJsonAsync(await AdminTokenAsync(), "/admin/serverinfo"))["features"]!;

    /// <summary>A keycloak.v2 login template exactly as this Keycloak ships it (from its themes jar).</summary>
    public async Task<byte[]> ReadUpstreamLoginTemplateAsync(string name, CancellationToken cancellationToken)
    {
        _themesJar ??= await _container.ReadFileAsync($"/opt/keycloak/lib/lib/main/org.keycloak.keycloak-themes-{Version}.jar", cancellationToken);
        using var jar = new ZipArchive(new MemoryStream(_themesJar), ZipArchiveMode.Read);
        var entry = jar.GetEntry($"theme/keycloak.v2/login/{name}")
            ?? throw new InvalidOperationException($"Keycloak {Version} has no keycloak.v2 login template {name}.");
        await using var stream = await entry.OpenAsync(cancellationToken);
        using var bytes = new MemoryStream();
        await stream.CopyToAsync(bytes, cancellationToken);
        return bytes.ToArray();
    }

    /// <summary>The status of an anonymous GET of a path under this realm.</summary>
    public async Task<HttpStatusCode> GetStatusAsync(string realmPath)
    {
        using var response = await _http.GetAsync(new Uri($"{Url}{realmPath}"));
        return response.StatusCode;
    }

    private async Task<string> AdminTokenAsync()
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = "password",
            ["client_id"] = "admin-cli",
            ["username"] = KeycloakBuilder.DefaultUsername,
            ["password"] = KeycloakBuilder.DefaultPassword,
        });
        using var response = await _http.PostAsync(AdminUri("/realms/master/protocol/openid-connect/token"), form);
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!["access_token"]!.GetValue<string>();
    }

    private async Task<JsonNode> GetClientAsync(string admin, string clientId) =>
        (await GetJsonAsync(admin, $"/admin/realms/{Name}/clients?clientId={clientId}")).AsArray().Single()!;

    private async Task<JsonNode> GetJsonAsync(string admin, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, AdminUri(path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);
        using var response = await _http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    private async Task SendAdminAsync(string admin, HttpMethod method, string path)
    {
        using var request = new HttpRequestMessage(method, AdminUri(path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);
        using var response = await _http.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    private Uri AdminUri(string path) => new($"{BaseAddress}{path}");

    private sealed class Removal(Func<Task> remove) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await remove();
    }
}
