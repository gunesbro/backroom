using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Npgsql;
using Backroom.Modules.CellManagement.Application;
using Backroom.Modules.CellManagement.Application.Provisioning;
using Backroom.Modules.CellManagement.Application.Registration;
using Backroom.Modules.CellManagement.Infrastructure;
using Backroom.Modules.ControlPlaneConnector.Infrastructure;
using Backroom.Modules.ControlPlaneConnector.Infrastructure.Registration;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;

namespace Backroom.Modules.CellManagement.IntegrationTests;

/// <summary>
/// Cell registration (docs/contracts/phase-1.md, "Cell registration") end to end: a control plane on a
/// test server, the cell's connector, and real Keycloak and PostgreSQL.
/// </summary>
[Trait("Category", "Privacy")]
[Collection(KeycloakSignInCollection.Name)]
public sealed class CellRegistrationTests(PostgresDatabase database, KeycloakRealm keycloak) : IAsyncLifetime
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private WebApplication _controlPlane = null!;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _controlPlane = await ControlPlaneUnderTest.StartAsync(database, keycloak, _time);

    public async ValueTask DisposeAsync() => await _controlPlane.DisposeAsync();

    [Fact]
    public async Task Register_WithValidKey_GivesTheCellWorkingCredentialsAsync()
    {
        var (cellId, key) = await ProvisionAsync();
        await using var cell = CreateCell(cellId, key);

        var registered = await cell.Worker.RegisterAsync(CancellationToken);
        var cellToken = new JsonWebToken(await cell.Credentials.GetAccessTokenAsync(CancellationToken));

        registered.Should().BeTrue();
        File.Exists(Path.Combine(cell.KeyDirectory, "cell-key.pem")).Should().BeTrue();
        cellToken.GetPayloadValue<string>("azp").Should().Be("cell-" + cellId);
        cellToken.Audiences.Should().Equal("control-plane");
        cellToken.TryGetPayloadValue<string>("sid", out _).Should().BeFalse("a cell token has no user session");
        (await CellStatusAsync(cellId)).Should().Be("registered");
    }

    [Fact]
    public async Task Register_WhenDone_LetsPeopleGetTokensForThatCellOnlyAsync()
    {
        var (cellId, key) = await ProvisionAsync();
        await using var cell = CreateCell(cellId, key);
        await cell.Worker.RegisterAsync(CancellationToken);

        var tokens = await keycloak.SignInAsync("alice@example.test", $"openid cell:{cellId}");

        new JsonWebToken(tokens.AccessToken).Audiences.Should().Equal("cell-" + cellId);
    }

    [Fact]
    public async Task Register_WhenDone_OffersTheCellScopeToTheWebAndDesktopClientsAsync()
    {
        var (cellId, key) = await ProvisionAsync();
        await using var cell = CreateCell(cellId, key);
        await cell.Worker.RegisterAsync(CancellationToken);

        var scopes = await keycloak.ClientScopesAsync();

        scopes["web"].Should().Contain("cell:" + cellId);
        scopes["desktop"].Should().Contain("cell:" + cellId);
        scopes["ops"].Should().NotContain("cell:" + cellId, "the operations panel works with the control plane only");
    }

    [Fact]
    public async Task Register_WithAnAlreadyUsedKey_IsRefusedAsync()
    {
        var (cellId, key) = await ProvisionAsync();
        await using var first = CreateCell(cellId, key);
        await first.Worker.RegisterAsync(CancellationToken);
        await using var second = CreateCell(cellId, key);

        var registered = await second.Worker.RegisterAsync(CancellationToken);

        registered.Should().BeFalse();
    }

    [Fact]
    public async Task Register_WhenTheKeyIsClaimedForAnotherPublicKey_IsRefusedAsync()
    {
        // Regression guard for the race: a second registration must not replace the first one's key.
        var (cellId, key) = await ProvisionAsync();
        await Registry().ClaimRegistrationKeyAsync(RegistrationKeys.Hash(key), "another-key-thumbprint", CancellationToken);
        await using var cell = CreateCell(cellId, key);

        var registered = await cell.Worker.RegisterAsync(CancellationToken);

        registered.Should().BeFalse();
        (await CellStatusAsync(cellId)).Should().Be("pending");
    }

    [Fact]
    public async Task Register_WhenRetriedWithTheSamePublicKey_SucceedsAsync()
    {
        var (cellId, key) = await ProvisionAsync();
        await using var cell = CreateCell(cellId, key);
        var thumbprint = CellPublicKey.TryParse(System.Text.Json.JsonSerializer.SerializeToElement(
            await cell.Credentials.GetPublicJwkAsync(CancellationToken)))!.Thumbprint;
        await Registry().ClaimRegistrationKeyAsync(RegistrationKeys.Hash(key), thumbprint, CancellationToken);

        var registered = await cell.Worker.RegisterAsync(CancellationToken);

        registered.Should().BeTrue();
    }

    [Fact]
    public async Task Register_WhenTheResponseWasLost_SucceedsOnRetryAsync()
    {
        // The control plane finished, but the cell never saw the answer and has no marker.
        var (cellId, key) = await ProvisionAsync();
        await using var cell = CreateCell(cellId, key);
        await cell.Worker.RegisterAsync(CancellationToken);
        File.Delete(Path.Combine(cell.KeyDirectory, "registered"));

        var registered = await cell.Worker.RegisterAsync(CancellationToken);

        registered.Should().BeTrue();
        File.Exists(Path.Combine(cell.KeyDirectory, "registered")).Should().BeTrue();
        (await CellStatusAsync(cellId)).Should().Be("registered");
    }

    [Fact]
    public async Task Register_WithTheDevelopmentSeedersAppHostKey_SucceedsAsync()
    {
        // The AppHost generates letters and digits only, not base64url of random bytes.
        var cellId = "t" + Guid.CreateVersion7().ToString("N")[^10..];
        var key = System.Security.Cryptography.RandomNumberGenerator.GetString("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789", 43);
        var options = new DevelopmentCellOptions();
        options.DevelopmentCells.Add(new DevelopmentCell { Id = cellId, Name = "Cell A", RegistrationKey = key });
        var seeder = new DevelopmentCellSeeder(_controlPlane.Services.GetRequiredService<CellRegistry>(), Options.Create(options), _time);
        await seeder.StartAsync(CancellationToken);
        await using var cell = CreateCell(cellId, key);

        var registered = await cell.Worker.RegisterAsync(CancellationToken);

        registered.Should().BeTrue();
    }

    [Fact]
    public async Task SeedDevelopmentCell_WhenTheCellExists_TakesTheConfiguredAddressAsync()
    {
        // A development cell seeded before the AppHost configured its address kept http://localhost/ for good.
        var cellId = "t" + Guid.CreateVersion7().ToString("N")[^10..];
        var key = System.Security.Cryptography.RandomNumberGenerator.GetString("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789", 43);
        var registry = _controlPlane.Services.GetRequiredService<CellRegistry>();
        var before = new DevelopmentCellOptions();
        before.DevelopmentCells.Add(new DevelopmentCell { Id = cellId, Name = "Cell A", RegistrationKey = key });
        await new DevelopmentCellSeeder(registry, Options.Create(before), _time).StartAsync(CancellationToken);
        var after = new DevelopmentCellOptions();
        after.DevelopmentCells.Add(new DevelopmentCell { Id = cellId, Name = "Cell A", RegistrationKey = key, PublicUrl = new Uri("http://localhost:5173/") });

        await new DevelopmentCellSeeder(registry, Options.Create(after), _time).StartAsync(CancellationToken);

        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand("SELECT public_url FROM cell_management.cells WHERE id = $1", connection);
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = cellId });
        ((string)(await command.ExecuteScalarAsync(CancellationToken))!).Should().Be("http://localhost:5173/");
    }

    [Fact]
    public async Task Register_WithAKeyFollowedByANewline_IsRefusedAsync()
    {
        var (_, key) = await ProvisionAsync();

        using var response = await PostRegistrationAsync(key + "\n", ValidJwk());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(CancellationToken)).Should().Contain("cell_management.registration_key_invalid");
    }

    [Fact]
    public async Task ProvisionCell_WithMissingFields_IsRefusedAsync()
    {
        var result = await _controlPlane.Services.GetRequiredService<CellProvisioningService>().ProvisionAsync(
            new NewCell("tmissing", null!, null!, null!, new Uri("https://cell.example.test")), CancellationToken);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(CellManagementErrors.CellInvalid);
    }

    [Fact]
    public async Task Register_KeepsTheKeyDirectoryPrivateToTheCellsUserAsync()
    {
        var (cellId, key) = await ProvisionAsync();
        await using var cell = CreateCell(cellId, key);

        await cell.Worker.RegisterAsync(CancellationToken);

        if (OperatingSystem.IsWindows())
        {
            AssertOnlyCurrentUserHasAccess(cell.KeyDirectory);
            AssertOnlyCurrentUserHasAccess(Path.Combine(cell.KeyDirectory, "cell-key.pem"));
        }
        else
        {
            File.GetUnixFileMode(cell.KeyDirectory).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.GetUnixFileMode(Path.Combine(cell.KeyDirectory, "cell-key.pem")).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void AssertOnlyCurrentUserHasAccess(string path)
    {
        System.Security.AccessControl.FileSystemSecurity acl = System.IO.Directory.Exists(path)
            ? new DirectoryInfo(path).GetAccessControl()
            : new FileInfo(path).GetAccessControl();
        var identities = acl.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(System.Security.Principal.SecurityIdentifier))
            .Cast<System.Security.AccessControl.FileSystemAccessRule>()
            .Select(rule => rule.IdentityReference)
            .Distinct()
            .ToList();
        identities.Should().ContainSingle().Which.Should().Be(System.Security.Principal.WindowsIdentity.GetCurrent().User);
    }

    [Theory]
    [InlineData("https://cp.example.test", "https://cp.example.test/api/v1/cells/register")]
    [InlineData("https://cp.example.test/", "https://cp.example.test/api/v1/cells/register")]
    [InlineData("https://example.test/control-plane", "https://example.test/control-plane/api/v1/cells/register")]
    public void RegistrationUri_KeepsTheControlPlaneBasePath(string controlPlaneUrl, string expected) =>
        CellRegistrationWorker.RegistrationUri(new Uri(controlPlaneUrl)).Should().Be(new Uri(expected));

    [Fact]
    public async Task Register_WithAnExpiredKey_IsRefusedAsync()
    {
        var (cellId, key) = await ProvisionAsync();
        _time.Advance(RegistrationKeys.Lifetime + TimeSpan.FromMinutes(1));
        await using var cell = CreateCell(cellId, key);

        var registered = await cell.Worker.RegisterAsync(CancellationToken);

        registered.Should().BeFalse();
    }

    public static TheoryData<string, string> InvalidRequests => new()
    {
        { "private key part", """{"kty":"EC","crv":"P-256","x":"f83OJ3D2xF1Bg8vub9tLe1gHMzV76e8Tus9uPHvRVEU","y":"x_FEzRu9m36HLN_tue659LNpXW6pCyStikYjKIWI5a0","d":"jpsQnnGQmL-YBIffH1136cspYG6-0iY7X1fCE9-E9LI"}""" },
        { "RSA key", """{"kty":"RSA","n":"0vx7agoebGcQSuuPiLJXZptN9nndrQmbXEps2aiAFbWhM78LhWx4","e":"AQAB"}""" },
        { "point not on the curve", """{"kty":"EC","crv":"P-256","x":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","y":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAE"}""" },
        { "other curve", """{"kty":"EC","crv":"P-384","x":"f83OJ3D2xF1Bg8vub9tLe1gHMzV76e8Tus9uPHvRVEU","y":"x_FEzRu9m36HLN_tue659LNpXW6pCyStikYjKIWI5a0"}""" },
    };

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public async Task Register_WithInvalidPublicKey_IsRefusedAsync(string reason, string jwk)
    {
        _ = reason;
        var (_, key) = await ProvisionAsync();

        using var response = await PostRegistrationAsync(key, JsonNode.Parse(jwk)!);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(CancellationToken)).Should().Contain("cell_management.public_key_invalid");
    }

    [Theory]
    [InlineData("not-a-key")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task Register_WithUnknownOrMalformedKey_GetsTheSameAnswerAsync(string registrationKey)
    {
        using var response = await PostRegistrationAsync(registrationKey, ValidJwk());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(CancellationToken)).Should().Contain("cell_management.registration_key_invalid");
    }

    [Fact]
    public async Task Register_TooOftenFromOneAddress_IsRateLimitedAsync()
    {
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 11; i++)
        {
            using var response = await PostRegistrationAsync("not-a-key", ValidJwk());
            statuses.Add(response.StatusCode);
        }

        statuses.Should().HaveCount(11).And.EndWith(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task ProvisionCell_WithoutOperationsToken_IsUnauthorizedAsync()
    {
        using var client = _controlPlane.GetTestClient();

        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/ops/cells", UriKind.Relative),
            new { id = "x", name = "x", type = "shared", region = "eu-test", publicUrl = "https://cell.example.test" },
            CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static JsonNode ValidJwk() =>
        JsonNode.Parse("""{"kty":"EC","crv":"P-256","x":"f83OJ3D2xF1Bg8vub9tLe1gHMzV76e8Tus9uPHvRVEU","y":"x_FEzRu9m36HLN_tue659LNpXW6pCyStikYjKIWI5a0"}""")!;

    private ICellRegistry Registry() => _controlPlane.Services.GetRequiredService<ICellRegistry>();

    private Task<(string CellId, string Key)> ProvisionAsync() => TestCell.ProvisionAsync(_controlPlane, CancellationToken);

    private async Task<HttpResponseMessage> PostRegistrationAsync(string registrationKey, JsonNode publicKey)
    {
        using var client = _controlPlane.GetTestClient();
        return await client.PostAsJsonAsync(
            new Uri("/api/v1/cells/register", UriKind.Relative),
            new JsonObject { ["registrationKey"] = registrationKey, ["publicKey"] = publicKey, ["version"] = "test" },
            CancellationToken);
    }

    private async Task<string> CellStatusAsync(string cellId)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand("SELECT status FROM cell_management.cells WHERE id = $1", connection);
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = cellId });
        return (string)(await command.ExecuteScalarAsync(CancellationToken))!;
    }

    private TestCell CreateCell(string cellId, string registrationKey) => TestCell.Create(_controlPlane, keycloak, cellId, registrationKey);
}
