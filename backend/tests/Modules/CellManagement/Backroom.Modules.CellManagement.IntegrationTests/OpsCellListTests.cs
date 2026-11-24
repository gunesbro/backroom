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
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Backroom.BuildingBlocks.Presentation.Authentication;
using Backroom.Modules.CellManagement.Application.Registration;
using Backroom.Modules.CellManagement.Contracts;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;

namespace Backroom.Modules.CellManagement.IntegrationTests;

/// <summary>
/// <c>GET /api/v1/ops/cells</c> (docs/contracts/phase-1.md, "Operations panel"): only the operations client
/// with role <c>ops</c> and a one-time-password sign-in; every call is an audit event.
/// </summary>
[Trait("Category", "Privacy")]
[Collection(KeycloakSignInCollection.Name)]
public sealed class OpsCellListTests(PostgresDatabase database, KeycloakRealm keycloak) : IAsyncLifetime
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly RSA _rsa = RSA.Create(2048);
    private readonly AuditLogs _logs = new();
    private readonly Dictionary<string, int> _organizations = new(StringComparer.Ordinal);
    private WebApplication _controlPlane = null!;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var key = new RsaSecurityKey(_rsa) { KeyId = "ops-test-key" };
        _controlPlane = await ControlPlaneUnderTest.StartAsync(database, keycloak, _time, services =>
        {
            services.AddSingleton<ICellOrganizationCounts>(new FixedOrganizationCounts(_organizations));
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
    public async Task ListCells_WithoutToken_IsUnauthorizedAsync()
    {
        using var response = await GetAsync(token: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("ops", false, true)]
    [InlineData("ops", true, false)]
    [InlineData("web", true, true)]
    public async Task ListCells_WithoutOpsClientRoleOrOneTimePassword_IsForbiddenAsync(string client, bool opsRole, bool otp)
    {
        using var response = await GetAsync(Token(client, opsRole, otp));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _logs.Messages.Should().NotContain(message => message.Contains("ops.cells.list", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ListCells_WithOperationsToken_ReturnsEveryFieldAsync()
    {
        var connected = await AddCellAsync("conn");
        var stale = await AddCellAsync("stale");
        var pending = await AddCellAsync("pend");
        await SqlAsync("UPDATE cell_management.cells SET status = 'registered', registered_at = now() WHERE id = ANY($1)", new[] { connected, stale });
        await Registry().RecordHeartbeatAsync(stale, "1.0.0", CancellationToken);
        _time.Advance(ICellCatalog.HealthyHeartbeatAge + TimeSpan.FromMinutes(1));
        await Registry().RecordHeartbeatAsync(connected, "1.2.3", CancellationToken);
        var heartbeat = _time.GetUtcNow();
        await InsertMessageAsync(connected, "pending");
        await InsertMessageAsync(connected, "pending");
        await InsertMessageAsync(connected, "acked");
        await InsertMessageAsync(connected, "rejected");
        await InsertMessageAsync(stale, "rejected");
        await InsertMessageAsync(stale, "rejected");
        _organizations[connected] = 3;

        using var response = await GetAsync(Token("ops", true, true));
        var cells = await ReadCellsAsync(response);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var one = cells[connected];
        one.GetProperty("type").GetString().Should().Be("shared");
        one.GetProperty("region").GetString().Should().Be("eu-test");
        one.GetProperty("status").GetString().Should().Be("registered");
        one.GetProperty("version").GetString().Should().Be("1.2.3");
        one.GetProperty("linkStatus").GetString().Should().Be("connected");
        one.GetProperty("lastHeartbeatAt").GetDateTimeOffset().Should().BeCloseTo(heartbeat, TimeSpan.FromSeconds(1));
        one.GetProperty("organizationCount").GetInt32().Should().Be(3);
        one.GetProperty("pendingMessages").GetInt32().Should().Be(2);
        one.GetProperty("rejectedMessages").GetInt32().Should().Be(1);

        var two = cells[stale];
        two.GetProperty("linkStatus").GetString().Should().Be("disconnected");
        two.GetProperty("organizationCount").GetInt32().Should().Be(0);
        two.GetProperty("pendingMessages").GetInt32().Should().Be(0);
        two.GetProperty("rejectedMessages").GetInt32().Should().Be(2);

        var third = cells[pending];
        third.GetProperty("status").GetString().Should().Be("pending");
        third.GetProperty("linkStatus").GetString().Should().Be("disconnected");
        third.GetProperty("lastHeartbeatAt").ValueKind.Should().Be(JsonValueKind.Null);
        third.GetProperty("version").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task ListCells_WithALimit_PagesByCellIdAsync()
    {
        var first = await AddCellAsync("page");
        var second = await AddCellAsync("page");
        var ids = new[] { first, second }.Order(StringComparer.Ordinal).ToArray();

        using var response = await GetAsync(Token("ops", true, true), $"?after={Uri.EscapeDataString(ids[0][..^1])}&limit=1");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.GetProperty("cells").GetArrayLength().Should().Be(1);
        body.GetProperty("next").GetString().Should().Be(body.GetProperty("cells")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task ListCells_WhenCalled_WritesAnAuditEventWithTheAccountAndNoOtherDataAsync()
    {
        var accountId = Guid.CreateVersion7();
        var secretCell = await AddCellAsync("audit");

        using var response = await GetAsync(Token("ops", true, true, accountId));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var audit = _logs.Messages.Should().ContainSingle(message => message.Contains("ops.cells.list", StringComparison.Ordinal)).Subject;
        audit.Should().Contain(accountId.ToString()).And.Contain("success");
        audit.Should().NotContain(secretCell);
    }

    private ICellRegistry Registry() => _controlPlane.Services.GetRequiredService<ICellRegistry>();

    private string Token(string client, bool opsRole, bool otp, Guid? accountId = null)
    {
        var now = DateTime.UtcNow;
        var claims = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [KeycloakClaims.Subject] = (accountId ?? Guid.NewGuid()).ToString(),
            [KeycloakClaims.SessionId] = "session-1",
            [KeycloakClaims.AuthorizedParty] = client,
            [KeycloakClaims.TokenType] = KeycloakClaims.AccessTokenType,
            [KeycloakClaims.RealmAccess] = new Dictionary<string, object> { ["roles"] = opsRole ? new[] { "ops" } : new[] { "default-roles-backroom" } },
            [KeycloakClaims.AuthenticationMethods] = otp ? new[] { "pwd", "otp" } : new[] { "pwd" },
        };
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = keycloak.Url,
            Audience = Audiences.ControlPlane,
            IssuedAt = now.AddMinutes(-1),
            NotBefore = now.AddMinutes(-1),
            Expires = now.AddMinutes(5),
            Claims = claims,
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(_rsa) { KeyId = "ops-test-key" }, SecurityAlgorithms.RsaSha256),
        });
    }

    private async Task<HttpResponseMessage> GetAsync(string? token, string query = "")
    {
        using var client = _controlPlane.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/v1/ops/cells" + query, UriKind.Relative));
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await client.SendAsync(request, CancellationToken);
    }

    private static async Task<Dictionary<string, JsonElement>> ReadCellsAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(CancellationToken);
        return body.GetProperty("cells").EnumerateArray().ToDictionary(cell => cell.GetProperty("id").GetString()!, cell => cell, StringComparer.Ordinal);
    }

    private async Task<string> AddCellAsync(string prefix)
    {
        var cellId = prefix + Guid.CreateVersion7().ToString("N")[^10..];
        var added = await Registry().AddCellAsync(
            new NewCell(cellId, "Test cell", "shared", "eu-test", new Uri("https://cell.example.test")),
            RandomNumberGenerator.GetBytes(32),
            _time.GetUtcNow().AddHours(1),
            CancellationToken);
        added.Should().BeTrue();
        return cellId;
    }

    private async Task InsertMessageAsync(string cellId, string state)
    {
        var sql = state switch
        {
            "pending" => "INSERT INTO cell_management.outbound_messages (message_id, cell_id, type, payload, created_at, next_attempt_at) VALUES ($1, $2, 'organization_provisioned', '\\x01', now(), now())",
            "acked" => "INSERT INTO cell_management.outbound_messages (message_id, cell_id, type, created_at, acked_at, next_attempt_at) VALUES ($1, $2, 'organization_provisioned', now(), now(), now())",
            _ => "INSERT INTO cell_management.outbound_messages (message_id, cell_id, type, created_at, rejected_code, rejected_at, next_attempt_at) VALUES ($1, $2, 'organization_provisioned', now(), 'x.rejected', now(), now())",
        };
        await using var dataSource = NpgsqlDataSource.Create(database.ApplicationConnectionString());
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = Guid.CreateVersion7() });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = cellId });
        await command.ExecuteNonQueryAsync(CancellationToken);
    }

    private async Task SqlAsync(string sql, string[] ids)
    {
        await using var dataSource = NpgsqlDataSource.Create(database.ApplicationConnectionString());
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.Add(new NpgsqlParameter<string[]> { TypedValue = ids });
        await command.ExecuteNonQueryAsync(CancellationToken);
    }

    private sealed class FixedOrganizationCounts(Dictionary<string, int> counts) : ICellOrganizationCounts
    {
        public Task<IReadOnlyDictionary<string, int>> CountByCellAsync(IReadOnlyCollection<string> cellIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, int>>(cellIds.Where(counts.ContainsKey).ToDictionary(id => id, id => counts[id], StringComparer.Ordinal));
    }

    private sealed class AuditLogs : ILoggerProvider
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_messages)
                {
                    return [.. _messages];
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new Collector(this);

        public void Dispose()
        {
        }

        private sealed class Collector(AuditLogs owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (owner._messages)
                {
                    owner._messages.Add(formatter(state, exception));
                }
            }
        }
    }
}
