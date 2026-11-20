using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Backroom.BuildingBlocks.Application.Organizations;
using Backroom.BuildingBlocks.Application.Persistence;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.BuildingBlocks.Presentation.Authentication;
using Backroom.BuildingBlocks.Presentation.Organizations;
using Backroom.BuildingBlocks.Presentation.Persistence;
using Backroom.Modules.Organization.Contracts;
using Backroom.Modules.Organization.Infrastructure;
using Backroom.Modules.Organization.Presentation;
using Backroom.Modules.Permissions.Contracts;
using Backroom.Modules.Permissions.Infrastructure;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;

namespace Backroom.Modules.Organization.IntegrationTests;

/// <summary>
/// The membership check and the first cell endpoints (docs/contracts/phase-1.md, "Cell") with real tokens and real
/// row-level security: who can read an organization, and what they see of its members.
/// </summary>
[Collection(CellCollection.Name)]
[Trait("Category", "Privacy")]
public sealed class OrganizationAccessTests(PostgresDatabase database, KeycloakRealm keycloak) : IAsyncLifetime
{
    private const string Alice = "alice@example.test";
    private const string Bob = "bob@example.test";

    private WebApplication _cell = null!;
    private HttpClient _client = null!;
    private string _cellId = null!;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await MigrateAsync();
        _cellId = await keycloak.CreateCellScopeAsync();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.WebHost.UseTestServer();
        builder.Configuration["Authentication:Authority"] = keycloak.Url;
        builder.Configuration["Database:ConnectionString"] = database.ApplicationConnectionString();
        builder.AddAccessTokenAuthentication(Audiences.Cell(_cellId), [KeycloakClients.Web, KeycloakClients.Desktop]);
        builder.Services.AddDatabaseSession().BindConfiguration(DatabaseOptions.SectionName);
        builder.Services.AddPermissions();
        builder.Services.AddOrganization();
        // Development would throw on a bad query string before the filters; production answers through them.
        builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);

        _cell = builder.Build();
        _cell.UseAuthentication();
        _cell.UseAuthorization();
        _cell.MapOrganizationEndpoints();
        _cell.MapGet("/api/v1/orgs/{orgId:guid}/guarded", () => TypedResults.Ok())
            .InOrganization()
            .RequirePermission(SystemRoles.MembersManageInvitations);
        // Everything InOrganization() does except the membership check, which RequirePermission must insist on.
        _cell.MapGet("/api/v1/orgs/{orgId:guid}/misconfigured", () => TypedResults.Ok())
            .AddEndpointFilter(static (context, next) =>
            {
                var organizationId = Guid.Parse((string)context.HttpContext.Request.RouteValues["orgId"]!);
                context.HttpContext.RequestServices.GetRequiredService<OrganizationContext>().Enter(organizationId);
                return next(context);
            })
            .WithUnitOfWork()
            .RequirePermission(SystemRoles.MembersManageInvitations);
        await _cell.StartAsync(CancellationToken);
        _client = _cell.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _cell.DisposeAsync();
    }

    [Fact]
    public async Task GetOrganization_AsAMember_ReturnsItAsync()
    {
        var organizationId = await ProvisionForAsync(Alice, "Acme");

        using var response = await GetAsAsync($"/api/v1/orgs/{organizationId}", Alice);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadJsonAsync(response);
        body["id"]!.GetValue<Guid>().Should().Be(organizationId);
        body["name"]!.GetValue<string>().Should().Be("Acme");
    }

    [Fact]
    public async Task GetOrganization_AsSomeoneElse_AnswersLikeAnUnknownOrganizationAsync()
    {
        var organizationId = await ProvisionForAsync(Alice, "Acme");

        using var someoneElses = await GetAsAsync($"/api/v1/orgs/{organizationId}", Bob);
        using var unknown = await GetAsAsync($"/api/v1/orgs/{Guid.CreateVersion7()}", Bob);

        someoneElses.StatusCode.Should().Be(HttpStatusCode.NotFound);
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var someoneElsesBody = await ReadJsonAsync(someoneElses);
        var unknownBody = await ReadJsonAsync(unknown);
        someoneElsesBody["code"]!.GetValue<string>().Should().Be("organization.not_found");
        someoneElsesBody.ToJsonString().Should().Be(unknownBody.ToJsonString(), "the answer must not reveal that the organization exists");
    }

    [Theory]
    [InlineData("removed")]
    [InlineData("deactivated")]
    public async Task GetOrganization_WhenTheMembershipEnded_IsRefusedAtOnceAsync(string status)
    {
        var organizationId = await ProvisionForAsync(Alice, "Acme");
        var token = await TokenAsync(Alice);
        using (var before = await GetWithTokenAsync($"/api/v1/orgs/{organizationId}", token))
        {
            before.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        await SetMemberStatusAsync(organizationId, await AccountAsync(Alice), status);

        // The same, still valid token: access ends with the membership, not with the token.
        using var after = await GetWithTokenAsync($"/api/v1/orgs/{organizationId}", token);
        using var unknown = await GetWithTokenAsync($"/api/v1/orgs/{Guid.CreateVersion7()}", token);
        after.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadJsonAsync(after)).ToJsonString().Should().Be((await ReadJsonAsync(unknown)).ToJsonString());
    }

    [Fact]
    public async Task GetOrganization_WithoutAToken_IsUnauthorizedAsync()
    {
        var organizationId = await ProvisionForAsync(Alice, "Acme");

        using var response = await GetWithTokenAsync($"/api/v1/orgs/{organizationId}", null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetOrganization_WithAnotherCellsToken_IsUnauthorizedAsync()
    {
        var organizationId = await ProvisionForAsync(Alice, "Acme");
        var otherCell = await keycloak.CreateCellScopeAsync();
        var (token, _) = await keycloak.SignInAsync(Alice, $"openid cell:{otherCell}");

        using var response = await GetWithTokenAsync($"/api/v1/orgs/{organizationId}", token);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ListMembers_ShowsDisplayNamesAndOnlyTheReadersOwnEmailAsync()
    {
        var organizationId = await ProvisionForAsync(Alice, "Acme");
        var other = Guid.CreateVersion7();
        await AddMemberAsync(organizationId, other, "Other Person", "other@example.test", "active");

        var body = await ReadJsonAsync(await GetAsAsync($"/api/v1/orgs/{organizationId}/members", Alice));
        var members = body["members"]!.AsArray();

        members.Should().HaveCount(2);
        var aliceId = await AccountAsync(Alice);
        var mine = members.Single(member => member!["accountId"]!.GetValue<Guid>() == aliceId)!;
        var theirs = members.Single(member => member!["accountId"]!.GetValue<Guid>() == other)!;
        mine["email"]!.GetValue<string>().Should().Be(Alice);
        theirs["displayName"]!.GetValue<string>().Should().Be("Other Person");
        theirs["email"].Should().BeNull("other people's emails are never shown");
        body.ToJsonString().Should().NotContain("other@example.test");
    }

    [Fact]
    public async Task ListMembers_LeavesOutEndedMembershipsAndOtherOrganizationsAsync()
    {
        var organizationId = await ProvisionForAsync(Alice, "Acme");
        var otherOrganization = await ProvisionForAsync(Alice, "Other");
        await AddMemberAsync(organizationId, Guid.CreateVersion7(), "Removed Person", "removed@example.test", "removed");
        await AddMemberAsync(otherOrganization, Guid.CreateVersion7(), "Elsewhere Person", "elsewhere@example.test", "active");

        var body = await ReadJsonAsync(await GetAsAsync($"/api/v1/orgs/{organizationId}/members", Alice));

        body["members"]!.AsArray().Should().ContainSingle();
        body.ToJsonString().Should().NotContain("Removed Person").And.NotContain("Elsewhere Person");
    }

    [Fact]
    public async Task ListMembers_ByPages_ReturnsEveryMemberOnceAsync()
    {
        var organizationId = await ProvisionForAsync(Alice, "Acme");
        for (var index = 0; index < 4; index++)
        {
            await AddMemberAsync(organizationId, Guid.CreateVersion7(), $"Member {index}", $"member{index}@example.test", "active");
        }

        var seen = new List<Guid>();
        string? after = null;
        var pages = 0;
        do
        {
            var query = after is null ? "limit=2" : $"limit=2&after={after}";
            var body = await ReadJsonAsync(await GetAsAsync($"/api/v1/orgs/{organizationId}/members?{query}", Alice));
            seen.AddRange(body["members"]!.AsArray().Select(member => member!["accountId"]!.GetValue<Guid>()));
            after = body["next"]?.GetValue<Guid>().ToString();
            pages++;
        }
        while (after is not null && pages < 10);

        pages.Should().Be(3);
        seen.Should().HaveCount(5).And.OnlyHaveUniqueItems().And.BeInAscendingOrder();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task ListMembers_WithAPageSizeOutOfRange_IsRefusedAsync(int limit)
    {
        var organizationId = await ProvisionForAsync(Alice, "Acme");

        using var response = await GetAsAsync($"/api/v1/orgs/{organizationId}/members?limit={limit}", Alice);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadJsonAsync(response))["code"]!.GetValue<string>().Should().Be("organization.page_invalid");
    }

    [Theory]
    [InlineData("after=notaguid")]
    [InlineData("limit=abc")]
    [InlineData("limit=99999999999")]
    public async Task ListMembers_WithAMalformedQuery_OnlyAMemberLearnsItIsBadAsync(string query)
    {
        var organizationId = await ProvisionForAsync(Alice, "Acme");

        using var member = await GetAsAsync($"/api/v1/orgs/{organizationId}/members?{query}", Alice);
        using var someoneElse = await GetAsAsync($"/api/v1/orgs/{organizationId}/members?{query}", Bob);

        member.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        someoneElse.StatusCode.Should().Be(HttpStatusCode.NotFound, "a bad query must not reveal that the organization exists");
    }

    [Fact]
    public async Task GuardedEndpoint_AsTheOwner_RunsAsync()
    {
        var organizationId = await ProvisionForAsync(Alice, "Acme");

        using var response = await GetAsAsync($"/api/v1/orgs/{organizationId}/guarded", Alice);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GuardedEndpoint_AsAMemberWithoutThePermission_IsForbiddenAsync()
    {
        var organizationId = await ProvisionForAsync(Alice, "Acme");
        var bob = await AccountAsync(Bob);
        await AddMemberAsync(organizationId, bob, "Bob", Bob, "active");
        await AssignRoleAsync(organizationId, bob, SystemRoles.Member);

        using var response = await GetAsAsync($"/api/v1/orgs/{organizationId}/guarded", Bob);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReadJsonAsync(response))["code"]!.GetValue<string>().Should().Be("permissions.forbidden");
    }

    [Fact]
    public async Task GuardedEndpoint_AsSomeoneElse_IsNotFoundRatherThanForbiddenAsync()
    {
        var organizationId = await ProvisionForAsync(Alice, "Acme");

        using var response = await GetAsAsync($"/api/v1/orgs/{organizationId}/guarded", Bob);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "a 403 would reveal that the organization exists");
    }

    [Fact]
    public async Task RequirePermission_WithoutTheMembershipCheck_NeverRunsTheEndpointAsync()
    {
        var organizationId = await ProvisionForAsync(Alice, "Acme");

        // The organization is entered, the transaction is open and Alice's role allows it: only the missing
        // membership check stops the endpoint.
        using var response = await GetAsAsync($"/api/v1/orgs/{organizationId}/misconfigured", Alice);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task GuardedEndpoint_ForAnOwnerWhoLeft_IsNotFoundAsync()
    {
        var organizationId = await ProvisionForAsync(Alice, "Acme");
        await SetMemberStatusAsync(organizationId, await AccountAsync(Alice), "removed");

        using var response = await GetAsAsync($"/api/v1/orgs/{organizationId}/guarded", Alice);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "a role left behind grants nothing without a membership");
    }

    [Fact]
    public async Task ListMembers_AsSomeoneElse_IsNotFoundAsync()
    {
        var organizationId = await ProvisionForAsync(Alice, "Acme");

        using var response = await GetAsAsync($"/api/v1/orgs/{organizationId}/members", Bob);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private async Task<Guid> AccountAsync(string username) => Guid.Parse(await keycloak.UserIdAsync(username));

    private async Task<string> TokenAsync(string username) =>
        (await keycloak.SignInAsync(username, $"openid cell:{_cellId}")).AccessToken;

    private async Task<HttpResponseMessage> GetAsAsync(string path, string username) => await GetWithTokenAsync(path, await TokenAsync(username));

    private async Task<HttpResponseMessage> GetWithTokenAsync(string path, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await _client.SendAsync(request, CancellationToken);
    }

    private static async Task<JsonNode> ReadJsonAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync(CancellationToken))!;

    /// <summary>An organization of its own for each test, with the account as its owner, as the control plane's message creates it.</summary>
    private async Task<Guid> ProvisionForAsync(string username, string name)
    {
        var organizationId = Guid.CreateVersion7();
        var owner = await AccountAsync(username);
        await using var scope = _cell.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<OrganizationContext>().Enter(organizationId);
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await unitOfWork.BeginAsync(CancellationToken);
        var result = await scope.ServiceProvider.GetRequiredService<IOrganizationProvisioning>().ProvisionAsync(
            new NewOrganization(organizationId, name, owner, username, username, "en-US"), CancellationToken);
        result.IsSuccess.Should().BeTrue();
        await unitOfWork.CommitAsync(CancellationToken);
        return organizationId;
    }

    private async Task AssignRoleAsync(Guid organizationId, Guid accountId, string role)
    {
        await using var scope = _cell.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<OrganizationContext>().Enter(organizationId);
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await unitOfWork.BeginAsync(CancellationToken);
        await scope.ServiceProvider.GetRequiredService<IPermissionsProvisioning>().AssignRoleAsync(organizationId, accountId, role, CancellationToken);
        await unitOfWork.CommitAsync(CancellationToken);
    }

    private async Task AddMemberAsync(Guid organizationId, Guid accountId, string displayName, string email, string status)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO organization.members (org_id, account_id, display_name, email, locale, status, version, joined_at)
            VALUES ($1, $2, $3, $4, 'en-US', $5, 1, now())
            """,
            connection);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organizationId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = accountId });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = displayName });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = email });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = status });
        await command.ExecuteNonQueryAsync(CancellationToken);
    }

    private async Task SetMemberStatusAsync(Guid organizationId, Guid accountId, string status)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand("UPDATE organization.members SET status = $3 WHERE org_id = $1 AND account_id = $2", connection);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organizationId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = accountId });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = status });
        (await command.ExecuteNonQueryAsync(CancellationToken)).Should().Be(1);
    }

    private Task MigrateAsync() => OrganizationSchema.MigrateOnceAsync(database, CancellationToken);
}
