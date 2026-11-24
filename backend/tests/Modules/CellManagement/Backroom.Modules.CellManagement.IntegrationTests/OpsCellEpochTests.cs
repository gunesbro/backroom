using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Backroom.BuildingBlocks.Presentation.Authentication;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;

namespace Backroom.Modules.CellManagement.IntegrationTests;

/// <summary>
/// <c>POST /api/v1/ops/cells/{id}/epoch</c> (docs/adr/0011, "Cell epoch"; docs/runbooks/cell-restore-and-failover.md):
/// only operations with role <c>ops</c> and a one-time-password sign-in may increment a cell's epoch, and every call is
/// an audit event naming the account and the cell.
/// </summary>
[Trait("Category", "Privacy")]
public sealed class OpsCellEpochTests(PostgresDatabase database, KeycloakRealm keycloak) : IAsyncLifetime
{
    private static readonly string[] s_opsRoles = ["ops"];
    private static readonly string[] s_defaultRoles = ["default-roles-backroom"];
    private static readonly string[] s_otpMethods = ["pwd", "otp"];
    private static readonly string[] s_passwordMethods = ["pwd"];

    private readonly RSA _rsa = RSA.Create(2048);
    private readonly AuditLogs _logs = new();
    private WebApplication _controlPlane = null!;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var key = new RsaSecurityKey(_rsa) { KeyId = "ops-epoch-test-key" };
        _controlPlane = await ControlPlaneUnderTest.StartAsync(database, keycloak, configureServices: services =>
        {
            services.AddLogging(logging => logging.AddProvider(_logs));
            services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                options.Configuration = new OpenIdConnectConfiguration { Issuer = keycloak.Url, SigningKeys = { key } });
        });
    }

    public async ValueTask DisposeAsync()
    {
        await _controlPlane.DisposeAsync();
        _rsa.Dispose();
        _logs.Dispose();
    }

    [Fact]
    public async Task IncrementEpoch_WithOperationsToken_IncrementsAndAuditsTheAccountAndCellAsync()
    {
        var cellId = (await TestCell.ProvisionAsync(_controlPlane, CancellationToken)).CellId;
        var accountId = Guid.CreateVersion7();

        using var first = await PostAsync(cellId, Token(opsRole: true, otp: true, accountId));
        using var second = await PostAsync(cellId, Token(opsRole: true, otp: true, accountId));

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        first.Headers.CacheControl!.NoStore.Should().BeTrue();
        (await first.Content.ReadFromJsonAsync<JsonElement>(CancellationToken)).GetProperty("epoch").GetString().Should().Be("2");
        (await second.Content.ReadFromJsonAsync<JsonElement>(CancellationToken)).GetProperty("epoch").GetString().Should().Be("3");
        (await StoredEpochAsync(cellId)).Should().Be(3);
        _logs.Messages.Where(message => message.Contains("ops.cells.epoch.increment", StringComparison.Ordinal)).Should().HaveCount(2)
            .And.AllSatisfy(audit => audit.Should().Contain(accountId.ToString()).And.Contain(cellId).And.Contain("success"));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task IncrementEpoch_WithoutOpsRoleOrOneTimePassword_IsForbiddenAuditedAndChangesNothingAsync(bool opsRole, bool otp)
    {
        var cellId = (await TestCell.ProvisionAsync(_controlPlane, CancellationToken)).CellId;

        using var response = await PostAsync(cellId, Token(opsRole, otp, Guid.CreateVersion7()));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await StoredEpochAsync(cellId)).Should().Be(1);
        _logs.Messages.Should().ContainSingle(message => message.Contains("ops.cells.epoch.increment", StringComparison.Ordinal))
            .Which.Should().Contain("denied.forbidden");
    }

    [Fact]
    public async Task IncrementEpoch_WithoutToken_IsUnauthorizedAndChangesNothingAsync()
    {
        var cellId = (await TestCell.ProvisionAsync(_controlPlane, CancellationToken)).CellId;

        using var response = await PostAsync(cellId, token: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await StoredEpochAsync(cellId)).Should().Be(1);
        _logs.Messages.Should().ContainSingle(message => message.Contains("ops.cells.epoch.increment", StringComparison.Ordinal))
            .Which.Should().Contain("denied.unauthenticated");
    }

    [Fact]
    public async Task IncrementEpoch_ForAnUnknownCell_IsNotFoundAndAuditedAsRejectedAsync()
    {
        using var response = await PostAsync("nocell" + Guid.CreateVersion7().ToString("N")[^10..], Token(opsRole: true, otp: true, Guid.CreateVersion7()));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadFromJsonAsync<JsonElement>(CancellationToken)).GetProperty("code").GetString()
            .Should().Be("cell_management.cell_not_found");
        _logs.Messages.Should().ContainSingle(message => message.Contains("ops.cells.epoch.increment", StringComparison.Ordinal))
            .Which.Should().Contain("rejected");
    }

    [Fact]
    public async Task IncrementEpoch_WithAnInvalidCellId_IsBadRequestAndAuditsNoIdAsync()
    {
        using var response = await PostAsync("Not_A_Cell", Token(opsRole: true, otp: true, Guid.CreateVersion7()));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _logs.Messages.Should().ContainSingle(message => message.Contains("ops.cells.epoch.increment", StringComparison.Ordinal))
            .Which.Should().Contain("invalid").And.NotContain("Not_A_Cell");
    }

    private string Token(bool opsRole, bool otp, Guid accountId)
    {
        var now = DateTime.UtcNow;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = keycloak.Url,
            Audience = Audiences.ControlPlane,
            IssuedAt = now.AddMinutes(-1),
            NotBefore = now.AddMinutes(-1),
            Expires = now.AddMinutes(5),
            Claims = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [KeycloakClaims.Subject] = accountId.ToString(),
                [KeycloakClaims.SessionId] = "session-1",
                [KeycloakClaims.AuthorizedParty] = "ops",
                [KeycloakClaims.TokenType] = KeycloakClaims.AccessTokenType,
                [KeycloakClaims.RealmAccess] = new Dictionary<string, object> { ["roles"] = opsRole ? s_opsRoles : s_defaultRoles },
                [KeycloakClaims.AuthenticationMethods] = otp ? s_otpMethods : s_passwordMethods,
            },
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(_rsa) { KeyId = "ops-epoch-test-key" }, SecurityAlgorithms.RsaSha256),
        });
    }

    private async Task<HttpResponseMessage> PostAsync(string cellId, string? token)
    {
        using var client = _controlPlane.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"/api/v1/ops/cells/{cellId}/epoch", UriKind.Relative));
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await client.SendAsync(request, CancellationToken);
    }

    private async Task<long> StoredEpochAsync(string cellId)
    {
        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var command = dataSource.CreateCommand("SELECT epoch FROM cell_management.cells WHERE id = $1");
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = cellId });
        return (long)(await command.ExecuteScalarAsync(CancellationToken))!;
    }
}
