using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.BuildingBlocks.Presentation.Authentication;
using Backroom.Modules.CellManagement.Contracts;
using Backroom.Modules.CellManagement.Infrastructure;
using Backroom.Modules.Directory.Infrastructure;
using Backroom.Modules.Directory.Presentation;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;

namespace Backroom.Modules.Directory.IntegrationTests;

/// <summary>
/// <c>GET /api/v1/me/organizations</c> and <c>GET /api/v1/organizations/{id}</c> (docs/contracts/phase-1.md,
/// flows 1 and 2): an account sees only its own organizations, never another account's.
/// </summary>
[Trait("Category", "Privacy")]
[Collection(DirectoryCollection.Name)]
public sealed class MyOrganizationsTests(PostgresDatabase database, KeycloakRealm keycloak) : IAsyncLifetime
{
    private WebApplication _controlPlane = null!;
    private HttpClient _alice = null!;
    private Guid _aliceId;
    private Guid _bobId;
    private string _cellId = null!;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _controlPlane = await DirectoryUnderTest.StartAsync(database, keycloak, CancellationToken);

        _aliceId = Guid.Parse(await keycloak.UserIdAsync("alice@example.test"));
        _bobId = Guid.Parse(await keycloak.UserIdAsync("bob@example.test"));
        var (token, _) = await keycloak.SignInAsync("alice@example.test", "openid control-plane");
        _alice = _controlPlane.GetTestClient();
        _alice.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        _cellId = "t" + Guid.CreateVersion7().ToString("N")[^10..];
        await ExecuteAsync(
            "INSERT INTO cell_management.cells (id, name, type, region, public_url, status, created_at) VALUES ($1, 'Test', 'shared', 'eu-test', $2, 'registered', now())",
            _cellId, $"https://{_cellId}.example.test/");
    }

    public async ValueTask DisposeAsync()
    {
        _alice.Dispose();
        await _controlPlane.DisposeAsync();
    }

    [Fact]
    public async Task ListMine_ReturnsOnlyActiveOrganizationsOfTheCallerWithTheirCellAsync()
    {
        var active = await AddOrganizationAsync("active", createdBy: _aliceId, member: (_aliceId, "active"));
        await AddOrganizationAsync("provisioning", createdBy: _aliceId, member: (_aliceId, "pending"));
        await AddOrganizationAsync("active", createdBy: _aliceId, member: (_aliceId, "removed"));
        await AddOrganizationAsync("active", createdBy: _bobId, member: (_bobId, "active"));

        var response = await _alice.GetFromJsonAsync<MyOrganizationsResponse>(new Uri("/api/v1/me/organizations", UriKind.Relative), CancellationToken);

        var mine = response!.Organizations.Where(organization => organization.CellId == _cellId).ToList();
        mine.Should().ContainSingle().Which.Should().Be(
            new AccountOrganizationResponse(active, "Org " + active, _cellId, new Uri($"https://{_cellId}.example.test/")));
    }

    [Fact]
    public async Task ListMine_ReturnsOrganizationsOldestFirstAsync()
    {
        var newer = await AddOrganizationAsync("active", createdBy: _aliceId, member: (_aliceId, "active"), createdAt: DateTimeOffset.UtcNow.AddDays(-1));
        var older = await AddOrganizationAsync("active", createdBy: _aliceId, member: (_aliceId, "active"), createdAt: DateTimeOffset.UtcNow.AddDays(-2));

        var response = await _alice.GetFromJsonAsync<MyOrganizationsResponse>(new Uri("/api/v1/me/organizations", UriKind.Relative), CancellationToken);

        response!.Organizations.Select(organization => organization.Id).Where(id => id == newer || id == older).Should().Equal(older, newer);
    }

    [Fact]
    public async Task ListMine_LeavesOutOrganizationsOnACellThatIsNotRegisteredAsync()
    {
        var pendingCell = "t" + Guid.CreateVersion7().ToString("N")[^10..];
        await ExecuteAsync(
            "INSERT INTO cell_management.cells (id, name, type, region, public_url, status, created_at) VALUES ($1, 'Pending', 'shared', 'eu-test', 'https://pending.example.test/', 'pending', now())",
            pendingCell);
        var onPendingCell = await AddOrganizationAsync("active", createdBy: _aliceId, member: (_aliceId, "active"), cellId: pendingCell);

        var response = await _alice.GetFromJsonAsync<MyOrganizationsResponse>(new Uri("/api/v1/me/organizations", UriKind.Relative), CancellationToken);

        response!.Organizations.Should().NotContain(organization => organization.Id == onPendingCell);
    }

    [Fact]
    public async Task GetState_ForTheCreatorAfterRemovalFromTheActiveOrganization_AnswersNotFoundAsync()
    {
        var id = await AddOrganizationAsync("active", createdBy: _aliceId, member: (_aliceId, "removed"));

        using var response = await _alice.GetAsync(new Uri($"/api/v1/organizations/{id}", UriKind.Relative), CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetState_ForTheCreatorWithoutMembershipWhileProvisioning_ReturnsProvisioningAsync()
    {
        var id = await AddOrganizationAsync("provisioning", createdBy: _aliceId, member: (_bobId, "active"));

        var state = await _alice.GetFromJsonAsync<OrganizationStateResponse>(new Uri($"/api/v1/organizations/{id}", UriKind.Relative), CancellationToken);

        state.Should().Be(new OrganizationStateResponse(id, "provisioning", null));
    }

    [Fact]
    public async Task GetState_WhenFailed_ReturnsTheCodeAsync()
    {
        var id = await AddOrganizationAsync("failed", createdBy: _aliceId, member: (_bobId, "active"), failureCode: "organization.slug_taken");

        var state = await _alice.GetFromJsonAsync<OrganizationStateResponse>(new Uri($"/api/v1/organizations/{id}", UriKind.Relative), CancellationToken);

        state.Should().Be(new OrganizationStateResponse(id, "failed", "organization.slug_taken"));
    }

    [Fact]
    public async Task GetState_OfAnotherAccountsOrganization_AnswersLikeAnUnknownOneAsync()
    {
        var others = await AddOrganizationAsync("active", createdBy: _bobId, member: (_bobId, "active"));

        using var other = await _alice.GetAsync(new Uri($"/api/v1/organizations/{others}", UriKind.Relative), CancellationToken);
        using var unknown = await _alice.GetAsync(new Uri($"/api/v1/organizations/{Guid.CreateVersion7()}", UriKind.Relative), CancellationToken);

        other.StatusCode.Should().Be(HttpStatusCode.NotFound);
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await other.Content.ReadAsStringAsync(CancellationToken)).Should().Be(await unknown.Content.ReadAsStringAsync(CancellationToken));
    }

    [Fact]
    public async Task GetState_AfterTheCallerWasRemoved_AnswersNotFoundAsync()
    {
        var id = await AddOrganizationAsync("active", createdBy: _bobId, member: (_aliceId, "removed"));

        using var response = await _alice.GetAsync(new Uri($"/api/v1/organizations/{id}", UriKind.Relative), CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task OrganizationScope_AcceptsOnlyOrganizationsMappedToTheCellAsync()
    {
        var mine = await AddOrganizationAsync("active", createdBy: _aliceId, member: (_aliceId, "active"));
        var alsoMine = await AddOrganizationAsync("provisioning", createdBy: _bobId, member: (_bobId, "pending"));
        var scope = _controlPlane.Services.GetRequiredService<ICellOrganizationScope>();

        (await scope.AreInCellAsync([mine, alsoMine], _cellId, CancellationToken)).Should().BeTrue();
        (await scope.AreInCellAsync([mine], "another-cell", CancellationToken)).Should().BeFalse();
        (await scope.AreInCellAsync([mine, Guid.CreateVersion7()], _cellId, CancellationToken)).Should().BeFalse("an unknown organization is in no cell");
    }

    [Theory]
    [InlineData("/api/v1/me/organizations")]
    [InlineData("/api/v1/organizations/0190a000-0000-7000-8000-000000000000")]
    public async Task Endpoints_WithoutToken_AreUnauthorizedAsync(string path)
    {
        using var anonymous = _controlPlane.GetTestClient();

        using var response = await anonymous.GetAsync(new Uri(path, UriKind.Relative), CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private async Task<Guid> AddOrganizationAsync(
        string status, Guid createdBy, (Guid AccountId, string Status) member, string? failureCode = null, string? cellId = null, DateTimeOffset? createdAt = null)
    {
        var id = Guid.CreateVersion7();
        await ExecuteAsync(
            "INSERT INTO directory.organizations (id, name, region, cell_id, status, failure_code, created_by, created_at) VALUES ($1, $2, 'eu-test', $3, $4, $5, $6, $7)",
            id, "Org " + id, cellId ?? _cellId, status, (object?)failureCode ?? DBNull.Value, createdBy, createdAt ?? DateTimeOffset.UtcNow);
        await ExecuteAsync(
            "INSERT INTO directory.memberships (account_id, org_id, status, version, updated_at) VALUES ($1, $2, $3, 1, now())",
            member.AccountId, id, member.Status);
        return id;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100", Justification = "Test seeding with constant SQL; values are parameters.")]
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
}
