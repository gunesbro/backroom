using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Backroom.BuildingBlocks.Application.Accounts;
using Backroom.BuildingBlocks.Application.Persistence;
using Backroom.ControlPlane.Contracts.V1;
using Backroom.Modules.CellManagement.Contracts;
using Backroom.Modules.Directory.Application.Organizations;
using Backroom.Modules.Directory.Presentation;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;

namespace Backroom.Modules.Directory.IntegrationTests;

/// <summary><c>POST /api/v1/organizations</c> and the cell's answer (docs/contracts/phase-1.md, flow 2).</summary>
[Collection(DirectoryCollection.Name)]
public sealed class OrganizationCreationTests(PostgresDatabase database, KeycloakRealm keycloak) : IAsyncLifetime
{
    private readonly string _region = "eu-t" + Guid.CreateVersion7().ToString("N")[^8..];
    private WebApplication _controlPlane = null!;
    private HttpClient _alice = null!;
    private Guid _aliceId;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _controlPlane = await DirectoryUnderTest.StartAsync(database, keycloak, CancellationToken);
        _aliceId = Guid.Parse(await keycloak.UserIdAsync("alice@example.test"));
        var (token, _) = await keycloak.SignInAsync("alice@example.test", "openid control-plane");
        _alice = _controlPlane.GetTestClient();
        _alice.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // The daily limit counts Alice's organizations: start from none.
        await ExecuteAsync("DELETE FROM directory.memberships WHERE org_id IN (SELECT id FROM directory.organizations WHERE created_by = $1)", _aliceId);
        await ExecuteAsync("DELETE FROM directory.organizations WHERE created_by = $1", _aliceId);
    }

    public async ValueTask DisposeAsync()
    {
        _alice.Dispose();
        await _controlPlane.DisposeAsync();
    }

    [Fact]
    public async Task Create_InAHealthyCell_StoresItAndSendsItToThatCellAsync()
    {
        var cellId = await AddCellAsync(heartbeatAgo: TimeSpan.FromSeconds(10));

        using var response = await CreateAsync("Acme");
        var created = await response.Content.ReadFromJsonAsync<CreateOrganizationResponse>(CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        created!.Status.Should().Be("provisioning");
        created.CellId.Should().Be(cellId);
        (await ScalarAsync<string>("SELECT status FROM directory.organizations WHERE id = $1", created.Id)).Should().Be("provisioning");
        (await ScalarAsync<string>("SELECT status FROM directory.memberships WHERE org_id = $1 AND account_id = $2", created.Id, _aliceId)).Should().Be("pending");
        var message = ConnectResponse.Parser.ParseFrom(await ScalarAsync<byte[]>(
            "SELECT payload FROM cell_management.outbound_messages WHERE cell_id = $1 AND type = 'OrganizationProvisioned'", cellId));
        message.OrganizationProvisioned.Should().BeEquivalentTo(new OrganizationProvisioned
        {
            OrganizationId = created.Id.ToString("D"),
            Name = "Acme",
            OwnerAccountId = _aliceId.ToString("D"),
            OwnerDisplayName = message.OrganizationProvisioned.OwnerDisplayName,
            OwnerEmail = "alice@example.test",
            OwnerLocale = message.OrganizationProvisioned.OwnerLocale,
        });
    }

    [Fact]
    public async Task Create_WithSeveralHealthyCells_PicksTheLeastLoadedAsync()
    {
        var busy = await AddCellAsync(heartbeatAgo: TimeSpan.FromSeconds(10), id: "a" + Guid.CreateVersion7().ToString("N")[^10..]);
        var quiet = await AddCellAsync(heartbeatAgo: TimeSpan.FromSeconds(10), id: "b" + Guid.CreateVersion7().ToString("N")[^10..]);
        await ExecuteAsync(
            "INSERT INTO directory.organizations (id, name, region, cell_id, status, created_by, created_at) VALUES ($1, 'Busy', $2, $3, 'active', $4, now())",
            Guid.CreateVersion7(), _region, busy, Guid.CreateVersion7());

        using var response = await CreateAsync("Acme");

        (await response.Content.ReadFromJsonAsync<CreateOrganizationResponse>(CancellationToken))!.CellId.Should().Be(quiet);
    }

    [Fact]
    public async Task Create_WithoutAHealthyCell_IsConflictAsync()
    {
        await AddCellAsync(heartbeatAgo: TimeSpan.FromMinutes(5));

        using var response = await CreateAsync("Acme");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync(CancellationToken)).Should().Contain("directory.no_cell_available");
    }

    [Fact]
    public async Task Create_MoreThanFiveInADay_IsRateLimitedAsync()
    {
        await AddCellAsync(heartbeatAgo: TimeSpan.FromSeconds(10));
        for (var i = 0; i < 5; i++)
        {
            using var created = await CreateAsync($"Acme {i}");
            created.StatusCode.Should().Be(HttpStatusCode.Accepted);
        }

        using var response = await CreateAsync("One too many");

        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter.Should().NotBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a\u0007b")]
    [InlineData("Acme\u202Egnp.exe")]
    [InlineData("\u200B")]
    [InlineData("a\u2028b")]
    [InlineData("a\uE000b")]
    [InlineData("\u3164\u3164")]
    [InlineData("\u2800")]
    [InlineData("\u200D")]
    [InlineData("Acme\u200C")]
    [InlineData("\u200DAcme")]
    [InlineData("A\u200C\u200Ccme")]
    public async Task Create_WithAnInvalidName_IsRefusedAsync(string name)
    {
        await AddCellAsync(heartbeatAgo: TimeSpan.FromSeconds(10));

        using var response = await CreateAsync(name);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_WithATooLongName_IsRefusedAsync()
    {
        await AddCellAsync(heartbeatAgo: TimeSpan.FromSeconds(10));

        using var response = await CreateAsync(new string('a', 101));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("\U0001F1F9\U0001F1F7", 100)]
    [InlineData("\U0001F44B\U0001F3FD", 100)]
    [InlineData("e\u0302\u0301", 100)]
    [InlineData("\U0001F3F3\uFE0F\u200D\U0001F308", 100)]
    public async Task Create_WithAMaximalNameOfMultiCodePointCharacters_IsStoredAsync(string character, int count)
    {
        // Length counts what people see (a flag, a waving hand with a skin tone, an "e" with two accents),
        // and the database accepts those names too. Regression: the column counted code points.
        await AddCellAsync(heartbeatAgo: TimeSpan.FromSeconds(10));

        using var response = await CreateAsync(string.Concat(Enumerable.Repeat(character, count)));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Theory]
    [InlineData("\u0645\u06CC\u200C\u062E\u0648\u0627\u0647\u0645")]
    [InlineData("\U0001F468\u200D\U0001F469\u200D\U0001F467 Family")]
    public async Task Create_WithJoinersInWordsOrEmoji_IsAcceptedAsync(string name)
    {
        await AddCellAsync(heartbeatAgo: TimeSpan.FromSeconds(10));

        using var response = await CreateAsync(name);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task Create_WithMoreCodePointsThanTheDatabaseAllows_IsRefusedNotFailedAsync()
    {
        // 100 five-code-point families are 500 code points: a 400, never a 500.
        await AddCellAsync(heartbeatAgo: TimeSpan.FromSeconds(10));

        using var response = await CreateAsync(string.Concat(Enumerable.Repeat("\U0001F468\u200D\U0001F469\u200D\U0001F467", 100)));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_WithAStackOfMarks_IsRefusedAsync()
    {
        await AddCellAsync(heartbeatAgo: TimeSpan.FromSeconds(10));

        using var response = await CreateAsync("a" + new string('\u0301', 20));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("EU")]
    [InlineData("e")]
    [InlineData("eu-test-")]
    [InlineData("eu test")]
    public async Task Create_WithAnInvalidRegion_IsRefusedAsync(string region)
    {
        using var response = await _alice.PostAsJsonAsync(
            new Uri("/api/v1/organizations", UriKind.Relative), new CreateOrganizationRequest("Acme", region), CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_WithAnUnverifiedEmail_IsForbiddenAsync()
    {
        // The realm requires a verified email to sign in at all, so this runs the service with such an account.
        await AddCellAsync(heartbeatAgo: TimeSpan.FromSeconds(10));
        await using var scope = _controlPlane.Services.CreateAsyncScope();
        var creation = ActivatorUtilities.CreateInstance<OrganizationCreation>(scope.ServiceProvider, new StubAccount(_aliceId, VerifiedEmail: null));

        var result = await creation.CreateAsync("Acme", _region, CancellationToken);

        result.Error!.Code.Should().Be("directory.email_not_verified");
    }

    [Theory]
    [InlineData(255)]
    [InlineData(-1)]
    public async Task Create_WithAnUnusableEmail_IsRefusedBeforeAnythingIsStoredAsync(int length)
    {
        // The cell stores at most 254 characters; -1 stands for an email with a control character.
        var cellId = await AddCellAsync(heartbeatAgo: TimeSpan.FromSeconds(10));
        var email = length < 0 ? "alice\u0007@example.test" : new string('a', length - "@example.test".Length) + "@example.test";
        await using var scope = _controlPlane.Services.CreateAsyncScope();
        var creation = ActivatorUtilities.CreateInstance<OrganizationCreation>(scope.ServiceProvider, new StubAccount(_aliceId, email));

        var result = await creation.CreateAsync("Acme", _region, CancellationToken);

        result.Error!.Code.Should().Be("directory.email_invalid");
        (await ScalarAsync<long>("SELECT count(*) FROM directory.organizations WHERE cell_id = $1", cellId)).Should().Be(0);
        (await ScalarAsync<long>("SELECT count(*) FROM cell_management.outbound_messages WHERE cell_id = $1", cellId)).Should().Be(0);
    }

    [Fact]
    public async Task Create_ConcurrentlyAtTheLimit_NeverPassesItAsync()
    {
        await AddCellAsync(heartbeatAgo: TimeSpan.FromSeconds(10));
        for (var i = 0; i < 4; i++)
        {
            using var created = await CreateAsync($"Acme {i}");
        }

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => CreateAsync($"Race {i}")));
        try
        {
            responses.Count(response => response.StatusCode == HttpStatusCode.Accepted).Should().BeLessThanOrEqualTo(1);
            (await ScalarAsync<long>("SELECT count(*) FROM directory.organizations WHERE created_by = $1", _aliceId)).Should().BeLessThanOrEqualTo(5);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task Create_WithoutALocaleInTheToken_SendsEnglishAsync()
    {
        var cellId = await AddCellAsync(heartbeatAgo: TimeSpan.FromSeconds(10));

        await CreatedAsync("Acme");

        var message = ConnectResponse.Parser.ParseFrom(await ScalarAsync<byte[]>(
            "SELECT payload FROM cell_management.outbound_messages WHERE cell_id = $1 AND type = 'OrganizationProvisioned'", cellId));
        message.OrganizationProvisioned.OwnerLocale.Should().Be("en-US");
    }

    [Fact]
    public async Task CellAcknowledgement_MakesTheOrganizationActiveAndListedAsync()
    {
        var cellId = await AddCellAsync(heartbeatAgo: TimeSpan.FromSeconds(10));
        var organization = await CreatedAsync("Acme");

        await SettleAsync(cellId, organization, rejectedCode: null);

        (await StateAsync(organization)).Should().Be(new OrganizationStateResponse(organization, "active", null));
        (await ScalarAsync<string>("SELECT status FROM directory.memberships WHERE org_id = $1 AND account_id = $2", organization, _aliceId)).Should().Be("active");
        var mine = await _alice.GetFromJsonAsync<MyOrganizationsResponse>(new Uri("/api/v1/me/organizations", UriKind.Relative), CancellationToken);
        mine!.Organizations.Should().Contain(entry => entry.Id == organization && entry.CellId == cellId);
    }

    [Fact]
    public async Task CellRejection_FailsTheOrganizationWithTheCodeAsync()
    {
        var cellId = await AddCellAsync(heartbeatAgo: TimeSpan.FromSeconds(10));
        var organization = await CreatedAsync("Acme");

        await SettleAsync(cellId, organization, rejectedCode: "organization.exists");

        (await StateAsync(organization)).Should().Be(new OrganizationStateResponse(organization, "failed", "organization.exists"));
        (await ScalarAsync<string>("SELECT status FROM directory.memberships WHERE org_id = $1 AND account_id = $2", organization, _aliceId)).Should().Be("removed");
    }

    [Fact]
    public async Task AcknowledgementFromAnotherCell_ChangesNothingAsync()
    {
        await AddCellAsync(heartbeatAgo: TimeSpan.FromSeconds(10));
        var organization = await CreatedAsync("Acme");

        await SettleAsync("another-cell", organization, rejectedCode: null);

        (await StateAsync(organization)).Status.Should().Be("provisioning");
    }

    private async Task<HttpResponseMessage> CreateAsync(string name) =>
        await _alice.PostAsJsonAsync(new Uri("/api/v1/organizations", UriKind.Relative), new CreateOrganizationRequest(name, _region), CancellationToken);

    private async Task<Guid> CreatedAsync(string name)
    {
        using var response = await CreateAsync(name);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        return (await response.Content.ReadFromJsonAsync<CreateOrganizationResponse>(CancellationToken))!.Id;
    }

    private async Task<OrganizationStateResponse> StateAsync(Guid organization) =>
        (await _alice.GetFromJsonAsync<OrganizationStateResponse>(new Uri($"/api/v1/organizations/{organization}", UriKind.Relative), CancellationToken))!;

    // What the link does when the cell answers: the outcome handler in a unit of work.
    private async Task SettleAsync(string cellId, Guid organization, string? rejectedCode)
    {
        await using var scope = _controlPlane.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await unitOfWork.BeginAsync(CancellationToken);
        var message = new CellMessage<OrganizationProvisioned>(
            Guid.CreateVersion7(), cellId, new OrganizationProvisioned { OrganizationId = organization.ToString("D") });
        foreach (var handler in scope.ServiceProvider.GetServices<ICellMessageOutcomeHandler<OrganizationProvisioned>>())
        {
            if (rejectedCode is null)
            {
                await handler.OnAcknowledgedAsync(message, CancellationToken);
            }
            else
            {
                await handler.OnRejectedAsync(message, rejectedCode, CancellationToken);
            }
        }

        await unitOfWork.CommitAsync(CancellationToken);
    }

    private async Task<string> AddCellAsync(TimeSpan heartbeatAgo, string? id = null)
    {
        var cellId = id ?? "t" + Guid.CreateVersion7().ToString("N")[^10..];
        await ExecuteAsync(
            "INSERT INTO cell_management.cells (id, name, type, region, public_url, status, created_at, last_heartbeat_at) VALUES ($1, 'Test', 'shared', $2, $3, 'registered', now(), $4)",
            cellId, _region, $"https://{cellId}.example.test/", DateTimeOffset.UtcNow - heartbeatAgo);
        return cellId;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100", Justification = "Test SQL is constant; values are parameters.")]
    private async Task<T> ScalarAsync<T>(string sql, params object[] values)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }

        return (T)(await command.ExecuteScalarAsync(CancellationToken))!;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100", Justification = "Test SQL is constant; values are parameters.")]
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

    private sealed class StubAccount(Guid accountId, string? VerifiedEmail) : ICurrentAccount
    {
        public Guid AccountId => accountId;

        public string? SessionId => "session";

        string? ICurrentAccount.VerifiedEmail => VerifiedEmail;

        public string? DisplayName => "Alice";

        public string? Locale => null;
    }
}
