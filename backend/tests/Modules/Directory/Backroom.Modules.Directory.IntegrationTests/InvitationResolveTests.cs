using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Backroom.Modules.Directory.Presentation;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;

namespace Backroom.Modules.Directory.IntegrationTests;

/// <summary>
/// Resolving an invitation code for the invite page (docs/contracts/phase-1.md, flow 3, step 4, and "Invitation
/// security"): the organization's name, the expiry and whether the signed-in account is the invited one, nothing
/// else; every code that cannot be used gets the same answer; nothing changes; attempts are limited.
/// </summary>
[Trait("Category", "Privacy")]
[Collection(DirectoryCollection.Name)]
public sealed class InvitationResolveTests(PostgresDatabase database, KeycloakRealm keycloak) : IAsyncLifetime
{
    private const string Alice = "alice@example.test";
    private const string Bob = "bob@example.test";

    private readonly byte[] _emailHmacKey = RandomNumberGenerator.GetBytes(32);
    private WebApplication _controlPlane = null!;
    private HttpClient _client = null!;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _controlPlane = await DirectoryUnderTest.StartAsync(database, keycloak, CancellationToken, emailHmacKey: Convert.ToBase64String(_emailHmacKey));
        _client = _controlPlane.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _controlPlane.DisposeAsync();
    }

    [Fact]
    public async Task Resolve_ForAPendingInvitationToTheAccount_ShowsTheOrganizationAndMatchesAsync()
    {
        var (code, _, expiresAt) = await AddInvitationAsync(Alice);

        using var response = await ResolveAsync(Alice, code);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        var body = await ReadJsonAsync(response);
        body["organizationName"]!.GetValue<string>().Should().Be("Acme");
        body["expiresAt"]!.GetValue<DateTimeOffset>().Should().BeCloseTo(expiresAt, TimeSpan.FromMilliseconds(1));
        body["matchesAccount"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task Resolve_SignedInWithAnotherAddress_DoesNotMatchAndRevealsNothingMoreAsync()
    {
        var (code, _, _) = await AddInvitationAsync(Alice);

        using var response = await ResolveAsync(Bob, code);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await ReadJsonAsync(response)).AsObject();
        body["matchesAccount"]!.GetValue<bool>().Should().BeFalse();
        body.Select(property => property.Key).Should().BeEquivalentTo(
            ["organizationName", "expiresAt", "matchesAccount"], "neither the invited address nor the inviter is shown");
    }

    public static TheoryData<string> UnusableCodes => new() { "unknown", "revoked", "used", "failed", "accepting", "expired", "provisioning", "malformed", "empty" };

    [Theory]
    [MemberData(nameof(UnusableCodes))]
    public async Task Resolve_ForACodeThatCannotBeUsed_AnswersTheSameInvalidAsync(string kind)
    {
        var code = kind switch
        {
            "unknown" => NewCode(),
            "malformed" => "not a code",
            "empty" => string.Empty,
            _ => await UnusableAsync(kind),
        };
        var unknown = NewCode();

        using var response = await ResolveAsync(Alice, code);
        using var reference = await ResolveAsync(Alice, unknown);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await ReadJsonAsync(response);
        body["code"]!.GetValue<string>().Should().Be("directory.invitation_invalid");
        response.Headers.CacheControl!.NoStore.Should().BeTrue("a refusal is not cached either");
        body["detail"]!.GetValue<string>().Should().Be((await ReadJsonAsync(reference))["detail"]!.GetValue<string>());
    }

    [Fact]
    public async Task Resolve_ChangesNothingAsync()
    {
        var (code, invitationId, _) = await AddInvitationAsync(Alice);
        var before = await ScalarAsync<DateTime>("SELECT updated_at FROM directory.invitations WHERE invitation_id = $1", invitationId);

        using var response = await ResolveAsync(Alice, code);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ScalarAsync<string>("SELECT status FROM directory.invitations WHERE invitation_id = $1", invitationId)).Should().Be("pending");
        (await ScalarAsync<DateTime>("SELECT updated_at FROM directory.invitations WHERE invitation_id = $1", invitationId)).Should().Be(before);
    }

    [Fact]
    public async Task Resolve_MoreThanTenTimesAMinute_IsRateLimitedAsync()
    {
        var (code, _, _) = await AddInvitationAsync(Alice);
        var token = await TokenAsync(Alice);
        for (var attempt = 0; attempt < InvitationAttemptLimits.AttemptsPerAccountPerMinute; attempt++)
        {
            using var allowed = await ResolveWithTokenAsync(token, code);
            allowed.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using var limited = await ResolveWithTokenAsync(token, code);

        limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await ReadJsonAsync(limited))["code"]!.GetValue<string>().Should().Be("directory.invitation_rate_limited");
        limited.Headers.RetryAfter.Should().NotBeNull();
    }

    [Fact]
    public async Task Resolve_WithoutASignedInAccount_IsUnauthorizedAsync()
    {
        var (code, _, _) = await AddInvitationAsync(Alice);

        using var response = await _client.PostAsJsonAsync(new Uri("/api/v1/invitations/resolve", UriKind.Relative), new { code }, CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Resolve_WithoutTheFeatureFlag_DoesNotExistAsync()
    {
        await using var controlPlane = await DirectoryUnderTest.StartAsync(database, keycloak, CancellationToken, invitations: false);
        using var client = controlPlane.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/invitations/resolve", UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(Alice));
        request.Content = JsonContent.Create(new { code = NewCode() });

        using var response = await client.SendAsync(request, CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync(CancellationToken)).Should().NotContain("invitation_invalid");
    }

    [Fact]
    public async Task Resolve_FromANetworkOverItsFailureLimit_RefusesOnlyFailuresAsync()
    {
        // Two failures allowed per network, so the test does not need a hundred requests.
        await using var controlPlane = await DirectoryUnderTest.StartAsync(
            database, keycloak, CancellationToken, emailHmacKey: Convert.ToBase64String(_emailHmacKey),
            configureServices: services => services.AddSingleton(new InvitationAttemptLimits(10, 1000, failuresPerNetworkPerHour: 2)));
        using var client = controlPlane.GetTestClient();
        var (valid, _, _) = await AddInvitationAsync(Alice);
        var token = await TokenAsync(Alice);
        for (var failure = 0; failure < 2; failure++)
        {
            using var refused = await ResolveWithTokenAsync(client, token, NewCode());
            refused.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        using var overLimit = await ResolveWithTokenAsync(client, token, NewCode());
        using var stillWorks = await ResolveWithTokenAsync(client, token, valid);

        overLimit.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await ReadJsonAsync(overLimit))["code"]!.GetValue<string>().Should().Be("directory.invitation_rate_limited");
        overLimit.Headers.RetryAfter!.Delta.Should().BePositive();
        overLimit.Headers.CacheControl!.NoStore.Should().BeTrue();
        stillWorks.StatusCode.Should().Be(HttpStatusCode.OK, "people behind the same network can still open their invitations");
    }

    private async Task<string> UnusableAsync(string kind)
    {
        var (code, invitationId, _) = await AddInvitationAsync(Alice);
        var sql = kind switch
        {
            "expired" => "UPDATE directory.invitations SET expires_at = now() - interval '1 minute' WHERE invitation_id = $1",
            "used" => "UPDATE directory.invitations SET status = 'used', accepting_account_id = invitation_id, accepted_at = now(), used_at = now() WHERE invitation_id = $1",
            // While accepting, even the accepting account sees it as unusable; the page follows GET /invitations/acceptance.
            "accepting" => "UPDATE directory.invitations SET status = 'accepting', accepting_account_id = invitation_id, accepted_at = now() WHERE invitation_id = $1",
            "failed" => "UPDATE directory.invitations SET status = 'failed', failure_code = 'organization.inviter_not_authorized' WHERE invitation_id = $1",
            "provisioning" => "UPDATE directory.organizations SET status = 'provisioning' WHERE id = (SELECT org_id FROM directory.invitations WHERE invitation_id = $1)",
            _ => "UPDATE directory.invitations SET status = 'revoked', revoked_at = now() WHERE invitation_id = $1",
        };
        await ExecuteAsync(sql, invitationId);
        return code;
    }

    // An invitation as a cell registers it: only the code's hash and the email's HMAC are stored.
    private async Task<(string Code, Guid InvitationId, DateTimeOffset ExpiresAt)> AddInvitationAsync(string email)
    {
        var organizationId = Guid.CreateVersion7();
        await ExecuteAsync(
            "INSERT INTO directory.organizations (id, name, region, cell_id, status, created_by, created_at) VALUES ($1, 'Acme', 'eu', 'cell-resolve', 'active', $1, now())",
            organizationId);
        var invitationId = Guid.CreateVersion7();
        var code = NewCode();
        var expiresAt = DateTimeOffset.UtcNow.AddDays(7);
        await ExecuteAsync(
            """
            INSERT INTO directory.invitations
                (invitation_id, org_id, cell_id, code_hash, email_hmac, role, expires_at, status, code_version, created_at, updated_at)
            VALUES ($1, $2, 'cell-resolve', $3, $4, 'member', $5, 'pending', 1, now(), now())
            """,
            invitationId,
            organizationId,
            SHA256.HashData(Encoding.ASCII.GetBytes(code)),
            EmailHmac(invitationId, email),
            expiresAt);
        return (code, invitationId, expiresAt);
    }

    private byte[] EmailHmac(Guid invitationId, string email)
    {
        var message = new byte[16 + email.Length];
        invitationId.TryWriteBytes(message, bigEndian: true, out _);
        Encoding.ASCII.GetBytes(email, message.AsSpan(16));
        return HMACSHA256.HashData(_emailHmacKey, message);
    }

    private static string NewCode() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private async Task<string> TokenAsync(string username) => (await keycloak.SignInAsync(username, "openid control-plane")).AccessToken;

    private async Task<HttpResponseMessage> ResolveAsync(string username, string code) => await ResolveWithTokenAsync(await TokenAsync(username), code);

    private Task<HttpResponseMessage> ResolveWithTokenAsync(string token, string code) => ResolveWithTokenAsync(_client, token, code);

    private static async Task<HttpResponseMessage> ResolveWithTokenAsync(HttpClient client, string token, string code)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/invitations/resolve", UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(new { code });
        return await client.SendAsync(request, CancellationToken);
    }

    private static async Task<JsonNode> ReadJsonAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync(CancellationToken))!;

    [SuppressMessage("Security", "CA2100", Justification = "Test SQL is constant; values are parameters.")]
    private async Task ExecuteAsync(string sql, params object[] values)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }

        await command.ExecuteNonQueryAsync(CancellationToken);
    }

    [SuppressMessage("Security", "CA2100", Justification = "Test SQL is constant; values are parameters.")]
    private async Task<T?> ScalarAsync<T>(string sql, Guid value)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = value });
        return await command.ExecuteScalarAsync(CancellationToken) is T result ? result : default;
    }
}
