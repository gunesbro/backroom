using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Backroom.EndToEndTests;

/// <summary>
/// One sign-in of a test user through the web client, as a browser does it (flow 1): the authorization code flow with
/// PKCE against the realm's sign-in page, with the session cookie in a cookie jar. Each instance is its own Keycloak
/// session. Refresh tokens are single-use, so token requests go one after another.
/// </summary>
internal sealed partial class SignedInPerson : IDisposable
{
    private const string ClientId = "web";

    // A redirect URI the realm allows for the web client; nothing listens there, the code is read from the redirect.
    private static readonly Uri s_redirectUri = new("http://localhost:5173/callback");

    private readonly HttpClient _http;
    private readonly Uri _openIdConnect;
    private readonly SemaphoreSlim _tokenQueue = new(1, 1);
    private string _refreshToken;

    private SignedInPerson(HttpClient http, Uri openIdConnect, string email, string refreshToken)
    {
        _http = http;
        _openIdConnect = openIdConnect;
        Email = email;
        _refreshToken = refreshToken;
    }

    public string Email { get; }

    /// <summary>Signs <paramref name="email"/> in with the sign-in scopes of "Audience per target".</summary>
    public static async Task<SignedInPerson> SignInAsync(
        EndToEndEnvironment environment, string email, IReadOnlyCollection<string> cellIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var scopes = string.Join(' ', ["openid", "control-plane", .. cellIds.Select(id => "cell:" + id)]);
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));

        using var browser = new HttpClient(new SocketsHttpHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false });
        var authorize = new Uri(
            environment.OpenIdConnectUrl,
            $"auth?client_id={ClientId}&response_type=code&redirect_uri={Uri.EscapeDataString(s_redirectUri.ToString())}" +
            $"&scope={Uri.EscapeDataString(scopes)}&state={state}&code_challenge={challenge}&code_challenge_method=S256");
        using var page = await browser.GetAsync(authorize, cancellationToken);
        var html = await page.Content.ReadAsStringAsync(cancellationToken);
        var form = LoginForm().Match(html);
        if (!form.Success)
        {
            throw new InvalidOperationException($"The sign-in page has no login form (HTTP {(int)page.StatusCode}).");
        }

        using var credentials = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = email,
            ["password"] = environment.TestUserPassword,
            ["credentialId"] = string.Empty,
        });
        using var signedIn = await browser.PostAsync(new Uri(WebUtility.HtmlDecode(form.Groups["action"].Value)), credentials, cancellationToken);
        var location = signedIn.Headers.Location;
        if (signedIn.StatusCode != HttpStatusCode.Found || location is null
            || !location.GetLeftPart(UriPartial.Path).Equals(s_redirectUri.ToString(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Signing in did not redirect to the app (HTTP {(int)signedIn.StatusCode}).");
        }

        var query = ParseQuery(location.Query);
        if (!string.Equals(query.GetValueOrDefault("state"), state, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The redirect carries another state.");
        }

        var code = query.GetValueOrDefault("code") ?? throw new InvalidOperationException("The redirect carries no code.");
        var tokens = await RequestTokensAsync(environment.Http, environment.OpenIdConnectUrl, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = s_redirectUri.ToString(),
            ["client_id"] = ClientId,
            ["code_verifier"] = verifier,
        }, cancellationToken);
        return new SignedInPerson(environment.Http, environment.OpenIdConnectUrl, email, tokens["refresh_token"]!.GetValue<string>());
    }

    /// <summary>An access token narrowed to one target: <c>control-plane</c> or <c>cell:{id}</c>.</summary>
    public async Task<AccessToken> TokenForAsync(string scope, CancellationToken cancellationToken)
    {
        await _tokenQueue.WaitAsync(cancellationToken);
        try
        {
            var tokens = await RequestTokensAsync(_http, _openIdConnect, new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = _refreshToken,
                ["client_id"] = ClientId,
                ["scope"] = scope,
            }, cancellationToken);
            _refreshToken = tokens["refresh_token"]!.GetValue<string>();
            return new AccessToken(tokens["access_token"]!.GetValue<string>());
        }
        finally
        {
            _tokenQueue.Release();
        }
    }

    /// <summary>Signs out as the apps do: Keycloak's logout endpoint with the refresh token, which ends the session (flow 5).</summary>
    public async Task SignOutAsync(CancellationToken cancellationToken)
    {
        await _tokenQueue.WaitAsync(cancellationToken);
        try
        {
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["refresh_token"] = _refreshToken,
            });
            using var response = await _http.PostAsync(new Uri(_openIdConnect, "logout"), form, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Signing out failed (HTTP {(int)response.StatusCode}).");
            }
        }
        finally
        {
            _tokenQueue.Release();
        }
    }

    public void Dispose() => _tokenQueue.Dispose();

    private static async Task<JsonObject> RequestTokensAsync(
        HttpClient http, Uri openIdConnect, Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(form);
        using var response = await http.PostAsync(new Uri(openIdConnect, "token"), content, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // The error code only; the body of a successful answer holds tokens and is never printed.
            var error = JsonNode.Parse(text)?["error"]?.GetValue<string>();
            throw new InvalidOperationException($"The token request failed (HTTP {(int)response.StatusCode}, {error}).");
        }

        return JsonNode.Parse(text)!.AsObject();
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            if (separator > 0)
            {
                result[Uri.UnescapeDataString(pair[..separator])] = Uri.UnescapeDataString(pair[(separator + 1)..]);
            }
        }

        return result;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [GeneratedRegex("<form id=\"kc-form-login\"[^>]*action=\"(?<action>[^\"]+)\"", RegexOptions.CultureInvariant)]
    private static partial Regex LoginForm();
}
