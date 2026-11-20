using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Backroom.BuildingBlocks.Application.Organizations;
using Backroom.BuildingBlocks.Application.Persistence;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.BuildingBlocks.Presentation.Authentication;
using Backroom.Modules.Organization.Contracts;
using Backroom.Modules.Organization.Infrastructure;
using Backroom.Modules.Organization.Infrastructure.Invitations;
using Backroom.Modules.Organization.Presentation;
using Backroom.Modules.Permissions.Contracts;
using Backroom.Modules.Permissions.Infrastructure;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;

namespace Backroom.Modules.Organization.IntegrationTests;

/// <summary>
/// Creating and listing invitations in the cell (docs/contracts/phase-1.md, flow 3, steps 1, 2 and 8) with real
/// tokens and real row-level security: who may invite whom into which role, the limits, and that no code or
/// hash ever leaves the cell's table.
/// </summary>
[Collection(CellCollection.Name)]
[Trait("Category", "Privacy")]
public sealed partial class InvitationTests(PostgresDatabase database, KeycloakRealm keycloak) : IAsyncLifetime
{
    private const string Alice = "alice@example.test";
    private const string Bob = "bob@example.test";
    private const string RateLimited = "organization.invitation_rate_limited";

    private static readonly string[] s_invitationProperties = ["createdAt", "createdBy", "email", "expiresAt", "id", "role"];

    private WebApplication _cell = null!;
    private HttpClient _client = null!;
    private string _cellId = null!;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await OrganizationSchema.MigrateOnceAsync(database, CancellationToken);
        _cellId = await keycloak.CreateCellScopeAsync();
        _cell = await StartCellAsync(invitations: true);
        _client = _cell.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _cell.DisposeAsync();
    }

    [Fact]
    public async Task Create_AsTheOwner_StoresAPendingInvitationAndAnswersWithoutACodeAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var before = DateTimeOffset.UtcNow;

        using var response = await InviteAsync(organizationId, Alice, "new.person@example.test");

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = (await ReadJsonAsync(response)).AsObject();
        body.Select(property => property.Key).Should().BeEquivalentTo(["id", "email", "role", "createdBy", "createdAt", "expiresAt"]);
        body["email"]!.GetValue<string>().Should().Be("new.person@example.test");
        body["role"]!.GetValue<string>().Should().Be(SystemRoles.Member);
        body["createdBy"]!.GetValue<Guid>().Should().Be(await AccountAsync(Alice));
        var createdAt = body["createdAt"]!.GetValue<DateTimeOffset>();
        createdAt.Should().BeOnOrAfter(before.AddSeconds(-1));
        body["expiresAt"]!.GetValue<DateTimeOffset>().Should().Be(createdAt.AddDays(7));

        var row = await QuerySingleAsync(
            "SELECT status, octet_length(salt), key_version, code_version, octet_length(code_hash), registered_at IS NULL FROM organization.invitations WHERE id = $1",
            body["id"]!.GetValue<Guid>());
        row.Should().Equal("pending", 16, 1, 1, 32, true);
    }

    [Fact]
    public async Task Create_PublishesTheEventWithoutCodeOrHashAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);

        using var response = await InviteAsync(organizationId, Alice, "new.person@example.test");

        var id = (await ReadJsonAsync(response))["id"]!.GetValue<Guid>();
        var events = await OutboxAsync(organizationId);
        events.Should().ContainSingle();
        events[0].Type.Should().Be(InvitationCreated.EventType);
        var payload = events[0].Payload.AsObject();
        payload.Select(property => property.Key).Should().BeEquivalentTo(
            ["event_id", "org_id", "occurred_at", "invitation_id", "code_version", "email", "role", "expires_at"]);
        payload["invitation_id"]!.GetValue<Guid>().Should().Be(id);
        payload["code_version"]!.GetValue<int>().Should().Be(1);
        payload["email"]!.GetValue<string>().Should().Be("new.person@example.test");
        payload["role"]!.GetValue<string>().Should().Be(SystemRoles.Member);
    }

    [Fact]
    public async Task Create_NormalizesTheEmailAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);

        using var response = await InviteAsync(organizationId, Alice, "  New.Person@BÜCHER.example ");

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        (await ReadJsonAsync(response))["email"]!.GetValue<string>().Should().Be("new.person@xn--bcher-kva.example");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("kim@localhost")]
    [InlineData("kim@example.test, lee@example.test")]
    [InlineData("Kim <kim@example.test>")]
    [InlineData("kim@example.test\r\nBcc: lee@example.test")]
    public async Task Create_WithAnythingButOneAddress_IsRefusedAsync(string? email)
    {
        var organizationId = await ProvisionForAsync(Alice);

        using var response = await InviteAsync(organizationId, Alice, email);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadJsonAsync(response))["code"]!.GetValue<string>().Should().Be("organization.invitation_email_invalid");
        (await CountInvitationsAsync(organizationId)).Should().Be(0);
    }

    [Theory]
    [InlineData("active", "organization.already_member")]
    [InlineData("deactivated", "organization.member_deactivated")]
    public async Task Create_ForAMember_IsRefusedAsync(string status, string code)
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddMemberAsync(organizationId, Guid.CreateVersion7(), "Kim@Example.test", status);

        using var response = await InviteAsync(organizationId, Alice, "kim@example.test");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadJsonAsync(response))["code"]!.GetValue<string>().Should().Be(code);
    }

    [Fact]
    public async Task Create_ForSomeoneWhoWasRemoved_InvitesThemAgainAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddMemberAsync(organizationId, Guid.CreateVersion7(), "kim@example.test", "removed");

        using var response = await InviteAsync(organizationId, Alice, "kim@example.test");

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Create_ForSomeoneInvitedByAnother_AsAMember_LeavesThatInvitationAloneAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddBobAsync(organizationId, SystemRoles.Member);
        using var byAlice = await InviteAsync(organizationId, Alice, "kim@example.test", SystemRoles.Admin);
        var aliceInvitation = (await ReadJsonAsync(byAlice))["id"]!.GetValue<Guid>();

        using var byBob = await InviteAsync(organizationId, Bob, "kim@example.test");

        byBob.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadJsonAsync(byBob))["code"]!.GetValue<string>().Should().Be("organization.invitation_pending");
        (await QuerySingleAsync("SELECT status, role FROM organization.invitations WHERE id = $1", aliceInvitation))
            .Should().Equal("pending", SystemRoles.Admin);
        (await CountInvitationsAsync(organizationId)).Should().Be(1, "a member cannot revoke or replace someone else's invitation");
    }

    [Fact]
    public async Task Create_ForSomeoneInvitedByAnother_AsAManager_ReplacesThatInvitationAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddBobAsync(organizationId, SystemRoles.Member);
        using var byBob = await InviteAsync(organizationId, Bob, "kim@example.test");
        var bobInvitation = (await ReadJsonAsync(byBob))["id"]!.GetValue<Guid>();

        using var byAlice = await InviteAsync(organizationId, Alice, "kim@example.test", SystemRoles.Admin);

        byAlice.StatusCode.Should().Be(HttpStatusCode.Created);
        (await QuerySingleAsync("SELECT status, revoked_by FROM organization.invitations WHERE id = $1", bobInvitation))
            .Should().Equal("revoked", await AccountAsync(Alice));
    }

    [Fact]
    public async Task Create_AfterAHundredAttempts_IsRateLimitedEvenWhenTheyWereRefusedAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddMemberAsync(organizationId, Guid.CreateVersion7(), "kim@example.test", "active");
        var token = await TokenAsync(Alice);
        for (var attempt = 0; attempt < 100; attempt++)
        {
            using var refused = await InviteWithTokenAsync(organizationId, token, "kim@example.test", null);
            refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        }

        // Without this limit, refusals would tell a member, address by address, who belongs to the organization.
        using var limited = await InviteWithTokenAsync(organizationId, token, "kim@example.test", null);

        await ShouldBeRateLimitedAsync(limited, TimeSpan.Zero, TimeSpan.FromDays(1));
    }

    [Fact]
    public async Task Create_WhenSeveralLimitsAreReached_WaitsForTheLastToClearAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var alice = await AccountAsync(Alice);
        // The inviter's limit clears in about an hour, the recipient's in about 22 hours.
        await SeedInvitationsAsync(organizationId, alice, 50, "revoked", TimeSpan.FromHours(23));
        await SeedToRecipientAsync(organizationId, Guid.CreateVersion7(), "kim@example.test", 3, TimeSpan.FromHours(2));

        using var response = await InviteAsync(organizationId, Alice, "kim@example.test");

        await ShouldBeRateLimitedAsync(response, TimeSpan.FromHours(21), TimeSpan.FromHours(22));
    }

    [Fact]
    public async Task Create_TwoAtOnceForTheLastPermit_CreatesOneAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await SeedInvitationsAsync(organizationId, await AccountAsync(Alice), 49, "revoked", TimeSpan.FromHours(2));
        var token = await TokenAsync(Alice);

        var responses = await Task.WhenAll(
            InviteWithTokenAsync(organizationId, token, "kim@example.test", null),
            InviteWithTokenAsync(organizationId, token, "lee@example.test", null));

        try
        {
            responses.Select(response => response.StatusCode).Should().BeEquivalentTo([HttpStatusCode.Created, HttpStatusCode.TooManyRequests]);
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
    public async Task Create_AgainForTheSameEmail_RevokesThePendingInvitationAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        using var first = await InviteAsync(organizationId, Alice, "kim@example.test");
        var firstId = (await ReadJsonAsync(first))["id"]!.GetValue<Guid>();

        using var second = await InviteAsync(organizationId, Alice, "KIM@example.test");

        second.StatusCode.Should().Be(HttpStatusCode.Created);
        var secondId = (await ReadJsonAsync(second))["id"]!.GetValue<Guid>();
        (await QuerySingleAsync("SELECT status, revoked_by, revoked_at IS NOT NULL FROM organization.invitations WHERE id = $1", firstId))
            .Should().Equal("revoked", await AccountAsync(Alice), true);
        (await QuerySingleAsync("SELECT status FROM organization.invitations WHERE id = $1", secondId)).Should().Equal("pending");
        var events = await OutboxAsync(organizationId);
        events.Select(entry => entry.Type).Should().BeEquivalentTo(
            [InvitationCreated.EventType, InvitationRevoked.EventType, InvitationCreated.EventType]);
        events.Single(entry => entry.Type == InvitationRevoked.EventType).Payload["invitation_id"]!.GetValue<Guid>().Should().Be(firstId);
    }

    [Fact]
    public async Task Create_TwiceAtOnceForTheSameEmail_LeavesOnePendingAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var token = await TokenAsync(Alice);

        var responses = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => InviteWithTokenAsync(organizationId, token, "kim@example.test", null)));

        try
        {
            responses.Should().OnlyContain(response => response.StatusCode == HttpStatusCode.Created);
            (await QuerySingleAsync("SELECT count(*) FROM organization.invitations WHERE org_id = $1 AND status = 'pending'", organizationId))
                .Should().Equal(1L);
            (await CountInvitationsAsync(organizationId)).Should().Be(3);
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
    public async Task Create_WithARole_AsTheOwner_InvitesIntoThatRoleAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);

        using var response = await InviteAsync(organizationId, Alice, "kim@example.test", SystemRoles.Admin);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        (await ReadJsonAsync(response))["role"]!.GetValue<string>().Should().Be(SystemRoles.Admin);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("Admin")]
    [InlineData("")]
    [InlineData("no-such-role")]
    public async Task Create_WithOwnerOrAnUnknownRole_IsRefusedAsync(string role)
    {
        var organizationId = await ProvisionForAsync(Alice);

        using var response = await InviteAsync(organizationId, Alice, "kim@example.test", role);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadJsonAsync(response))["code"]!.GetValue<string>().Should().Be("organization.invitation_role_invalid");
    }

    [Theory]
    [InlineData("member")]
    [InlineData("admin")]
    [InlineData("owner")]
    public async Task Create_WithARole_AsAMemberWhoMayNotChooseIt_IsForbiddenAsync(string role)
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddBobAsync(organizationId, SystemRoles.Member);

        using var response = await InviteAsync(organizationId, Bob, "kim@example.test", role);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReadJsonAsync(response))["code"]!.GetValue<string>().Should().Be("permissions.forbidden");
        (await CountInvitationsAsync(organizationId)).Should().Be(0);
    }

    [Fact]
    public async Task Create_WithoutARole_AsAMember_InvitesAsMemberAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddBobAsync(organizationId, SystemRoles.Member);

        using var response = await InviteAsync(organizationId, Bob, "kim@example.test");

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        (await ReadJsonAsync(response))["role"]!.GetValue<string>().Should().Be(SystemRoles.Member);
    }

    [Fact]
    public async Task Create_AsAMemberWithoutAnyRole_IsForbiddenAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddMemberAsync(organizationId, await AccountAsync(Bob), Bob, "active");

        using var response = await InviteAsync(organizationId, Bob, "kim@example.test");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Create_AsSomeoneElse_AnswersLikeAnUnknownOrganizationAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);

        using var response = await InviteAsync(organizationId, Bob, "kim@example.test");
        using var anonymous = await InviteWithTokenAsync(organizationId, null, "kim@example.test", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadJsonAsync(response))["code"]!.GetValue<string>().Should().Be("organization.not_found");
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await CountInvitationsAsync(organizationId)).Should().Be(0);
    }

    [Fact]
    public async Task Create_WithAnOversizedEmail_IsRefusedAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);

        using var response = await InviteAsync(organizationId, Alice, new string('a', 5000) + "@example.test");

        // The test server does not apply the request size limit that Kestrel enforces first (413); the email rule still refuses it.
        ((int)response.StatusCode).Should().BeOneOf(400, 413);
        (await CountInvitationsAsync(organizationId)).Should().Be(0);
    }

    [Fact]
    public async Task Create_AfterFiftyByTheSameInviterInADay_IsRateLimitedAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var alice = await AccountAsync(Alice);
        await SeedInvitationsAsync(organizationId, alice, 49, "revoked", TimeSpan.FromHours(2));

        using var last = await InviteAsync(organizationId, Alice, "kim@example.test");
        using var oneTooMany = await InviteAsync(organizationId, Alice, "lee@example.test");

        last.StatusCode.Should().Be(HttpStatusCode.Created);
        await ShouldBeRateLimitedAsync(oneTooMany, TimeSpan.FromHours(21), TimeSpan.FromHours(22));
    }

    [Fact]
    public async Task Create_AfterTwoHundredInTheOrganizationInADay_IsRateLimitedAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await SeedInvitationsAsync(organizationId, Guid.CreateVersion7(), 200, "revoked", TimeSpan.FromHours(23));

        using var response = await InviteAsync(organizationId, Alice, "kim@example.test");

        await ShouldBeRateLimitedAsync(response, TimeSpan.Zero, TimeSpan.FromHours(1));
    }

    [Fact]
    public async Task Create_ForInvitationsOlderThanADay_IsNotRateLimitedAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await SeedInvitationsAsync(organizationId, await AccountAsync(Alice), 200, "revoked", TimeSpan.FromHours(25));

        using var response = await InviteAsync(organizationId, Alice, "kim@example.test");

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Create_ForTheFourthTimeToOneAddressInADay_IsRateLimitedAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var response = await InviteAsync(organizationId, Alice, "kim@example.test");
            response.StatusCode.Should().Be(HttpStatusCode.Created);
        }

        using var fourth = await InviteAsync(organizationId, Alice, "kim@example.test");
        using var someoneElse = await InviteAsync(organizationId, Alice, "lee@example.test");

        await ShouldBeRateLimitedAsync(fourth, TimeSpan.FromHours(23), TimeSpan.FromHours(24));
        someoneElse.StatusCode.Should().Be(HttpStatusCode.Created);
        (await QuerySingleAsync("SELECT count(*) FROM organization.invitations WHERE org_id = $1 AND email = 'kim@example.test' AND status = 'pending'", organizationId))
            .Should().Equal([1L], "a refused request changes nothing");
    }

    [Fact]
    public async Task Create_WithFiveHundredPending_IsRateLimitedAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await SeedInvitationsAsync(organizationId, Guid.CreateVersion7(), 500, "pending", TimeSpan.FromDays(3));

        using var response = await InviteAsync(organizationId, Alice, "kim@example.test");
        // Replacing one of the pending invitations does not add to them.
        using var replacement = await InviteAsync(organizationId, Alice, "seed-1@example.test");

        await ShouldBeRateLimitedAsync(response, TimeSpan.FromMinutes(59), TimeSpan.FromHours(1));
        replacement.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Create_InAnotherOrganization_IsNotAffectedByThisOnesInvitationsAsync()
    {
        var first = await ProvisionForAsync(Alice);
        var second = await ProvisionForAsync(Alice);
        using var inFirst = await InviteAsync(first, Alice, "kim@example.test");
        var firstId = (await ReadJsonAsync(inFirst))["id"]!.GetValue<Guid>();

        using var inSecond = await InviteAsync(second, Alice, "kim@example.test");

        inSecond.StatusCode.Should().Be(HttpStatusCode.Created);
        (await QuerySingleAsync("SELECT status FROM organization.invitations WHERE id = $1", firstId)).Should().Equal("pending");
    }

    [Fact]
    public async Task List_AsAManager_ShowsEveryPendingInvitationWithoutCodesAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddBobAsync(organizationId, SystemRoles.Member);
        using var byAlice = await InviteAsync(organizationId, Alice, "kim@example.test");
        using var byBob = await InviteAsync(organizationId, Bob, "lee@example.test");
        using var replaced = await InviteAsync(organizationId, Bob, "lee@example.test");

        using var response = await GetAsync($"/api/v1/orgs/{organizationId}/invitations", Alice);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadJsonAsync(response);
        var invitations = body["invitations"]!.AsArray();
        invitations.Select(invitation => invitation!["email"]!.GetValue<string>()).Should().BeEquivalentTo(["kim@example.test", "lee@example.test"]);
        invitations.Should().OnlyContain(invitation =>
            invitation!.AsObject().Select(property => property.Key).Order().SequenceEqual(s_invitationProperties));
        body["next"].Should().BeNull();
    }

    [Fact]
    public async Task List_AsAMember_ShowsOnlyTheirOwnAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddBobAsync(organizationId, SystemRoles.Member);
        using var byAlice = await InviteAsync(organizationId, Alice, "kim@example.test");
        using var byBob = await InviteAsync(organizationId, Bob, "lee@example.test");

        using var response = await GetAsync($"/api/v1/orgs/{organizationId}/invitations", Bob);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var invitations = (await ReadJsonAsync(response))["invitations"]!.AsArray();
        invitations.Select(invitation => invitation!["email"]!.GetValue<string>()).Should().Equal("lee@example.test");
    }

    [Fact]
    public async Task List_WithoutAnInvitationPermission_IsForbiddenAndForOutsidersNotFoundAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var other = await ProvisionForAsync(Alice);
        using var invited = await InviteAsync(organizationId, Alice, "kim@example.test");

        using var outsider = await GetAsync($"/api/v1/orgs/{organizationId}/invitations", Bob);
        await AddMemberAsync(organizationId, await AccountAsync(Bob), Bob, "active");
        using var withoutRole = await GetAsync($"/api/v1/orgs/{organizationId}/invitations", Bob);
        using var otherOrganization = await GetAsync($"/api/v1/orgs/{other}/invitations", Alice);

        outsider.StatusCode.Should().Be(HttpStatusCode.NotFound);
        withoutRole.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReadJsonAsync(otherOrganization))["invitations"]!.AsArray().Should().BeEmpty("invitations stay in their organization");
    }

    [Fact]
    public async Task List_ByPages_ReturnsEveryInvitationOnceAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await SeedInvitationsAsync(organizationId, await AccountAsync(Alice), 5, "pending", TimeSpan.FromDays(2));

        var seen = new List<Guid>();
        Guid? after = null;
        do
        {
            using var response = await GetAsync($"/api/v1/orgs/{organizationId}/invitations?limit=2{(after is null ? string.Empty : $"&after={after}")}", Alice);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = await ReadJsonAsync(response);
            seen.AddRange(body["invitations"]!.AsArray().Select(invitation => invitation!["id"]!.GetValue<Guid>()));
            after = body["next"]?.GetValue<Guid>();
        }
        while (after is not null);

        seen.Should().HaveCount(5).And.OnlyHaveUniqueItems();
        using var tooMany = await GetAsync($"/api/v1/orgs/{organizationId}/invitations?limit=101", Alice);
        tooMany.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Endpoints_WithoutTheFeatureFlag_DoNotExistAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await using var cell = await StartCellAsync(invitations: false);
        using var client = cell.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"/api/v1/orgs/{organizationId}/invitations", UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(Alice));

        using var response = await client.SendAsync(request, CancellationToken);
        using var organization = await GetAsync($"/api/v1/orgs/{organizationId}", Alice);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        organization.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task<WebApplication> StartCellAsync(bool invitations)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.WebHost.UseTestServer();
        builder.Configuration["Authentication:Authority"] = keycloak.Url;
        builder.Configuration["Database:ConnectionString"] = database.ApplicationConnectionString();
        builder.Configuration[OrganizationEndpoints.InvitationsFlag] = invitations ? "true" : "false";
        builder.Configuration[$"{InvitationKeyOptions.SectionName}:CurrentVersion"] = "1";
        builder.Configuration[$"{InvitationKeyOptions.SectionName}:Keys:1"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        builder.AddAccessTokenAuthentication(Audiences.Cell(_cellId), [KeycloakClients.Web, KeycloakClients.Desktop]);
        builder.Services.AddDatabaseSession().BindConfiguration(DatabaseOptions.SectionName);
        builder.Services.AddPermissions();
        builder.Services.AddOrganization();
        builder.Services.AddOrganizationInvitations().BindConfiguration(InvitationKeyOptions.SectionName);
        builder.Configuration[$"{InvitationLinkOptions.SectionName}:WebOrigin"] = WebOrigin;
        builder.Services.AddOrganizationInvitationLinks().BindConfiguration(InvitationLinkOptions.SectionName);
        builder.Services.AddOrganizationEndpoints();
        // Development would throw on a bad request before the filters; production answers through them.
        builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);

        var cell = builder.Build();
        cell.UseAuthentication();
        cell.UseAuthorization();
        cell.UseRateLimiter();
        cell.MapOrganizationEndpoints();
        await cell.StartAsync(CancellationToken);
        return cell;
    }

    private static async Task ShouldBeRateLimitedAsync(HttpResponseMessage response, TimeSpan atLeast, TimeSpan atMost)
    {
        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await ReadJsonAsync(response))["code"]!.GetValue<string>().Should().Be(RateLimited);
        var retryAfter = response.Headers.RetryAfter!.Delta!.Value;
        retryAfter.Should().BeGreaterThan(atLeast).And.BeLessThanOrEqualTo(atMost);
    }

    private async Task<Guid> AccountAsync(string username) => Guid.Parse(await keycloak.UserIdAsync(username));

    private async Task<string> TokenAsync(string username) =>
        (await keycloak.SignInAsync(username, $"openid cell:{_cellId}")).AccessToken;

    private async Task<HttpResponseMessage> InviteAsync(Guid organizationId, string username, string? email, string? role = null) =>
        await InviteWithTokenAsync(organizationId, await TokenAsync(username), email, role);

    private async Task<HttpResponseMessage> InviteWithTokenAsync(Guid organizationId, string? token, string? email, string? role)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"/api/v1/orgs/{organizationId}/invitations", UriKind.Relative));
        request.Content = JsonContent.Create(new { email, role });
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await _client.SendAsync(request, CancellationToken);
    }

    private async Task<HttpResponseMessage> GetAsync(string path, string username)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(username));
        return await _client.SendAsync(request, CancellationToken);
    }

    private static async Task<JsonNode> ReadJsonAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync(CancellationToken))!;

    /// <summary>An organization of its own for each test, with the account as its owner.</summary>
    private async Task<Guid> ProvisionForAsync(string username)
    {
        var organizationId = Guid.CreateVersion7();
        var owner = await AccountAsync(username);
        await InOrganizationAsync(organizationId, async services =>
        {
            var result = await services.GetRequiredService<IOrganizationProvisioning>().ProvisionAsync(
                new NewOrganization(organizationId, "Acme", owner, username, username, "en-US"), CancellationToken);
            result.IsSuccess.Should().BeTrue();
        });
        return organizationId;
    }

    private async Task AddBobAsync(Guid organizationId, string role)
    {
        var bob = await AccountAsync(Bob);
        await AddMemberAsync(organizationId, bob, Bob, "active");
        await InOrganizationAsync(organizationId, services =>
            services.GetRequiredService<IPermissionsProvisioning>().AssignRoleAsync(organizationId, bob, role, CancellationToken));
    }

    private async Task InOrganizationAsync(Guid organizationId, Func<IServiceProvider, Task> work)
    {
        await using var scope = _cell.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<OrganizationContext>().Enter(organizationId);
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await unitOfWork.BeginAsync(CancellationToken);
        await work(scope.ServiceProvider);
        await unitOfWork.CommitAsync(CancellationToken);
    }

    private Task AddMemberAsync(Guid organizationId, Guid accountId, string email, string status) =>
        ExecuteAsync(
            """
            INSERT INTO organization.members (org_id, account_id, display_name, email, locale, status, version, joined_at)
            VALUES ($1, $2, 'Someone', $3, 'en-US', $4, 1, now())
            """,
            organizationId, accountId, email, status);

    /// <summary>Invitations to <c>seed-N@example.test</c> created <paramref name="age"/> ago, as rows only.</summary>
    private Task SeedInvitationsAsync(Guid organizationId, Guid createdBy, int count, string status, TimeSpan age) =>
        ExecuteAsync(
            """
            INSERT INTO organization.invitations
                (id, org_id, email, role, salt, key_version, code_version, code_hash, status, created_by, created_at, expires_at)
            SELECT uuidv7(), $1, 'seed-' || n || '@example.test', 'member', decode(repeat('00', 16), 'hex'), 1, 1,
                   decode(repeat('00', 32), 'hex'), $4, $2, now() - $5, now() - $5 + interval '7 days'
            FROM generate_series(1, $3) AS n
            """,
            organizationId, createdBy, count, status, age);

    private Task SeedToRecipientAsync(Guid organizationId, Guid createdBy, string email, int count, TimeSpan age) =>
        ExecuteAsync(
            """
            INSERT INTO organization.invitations
                (id, org_id, email, role, salt, key_version, code_version, code_hash, status, created_by, created_at, expires_at)
            SELECT uuidv7(), $1, $3, 'member', decode(repeat('00', 16), 'hex'), 1, 1,
                   decode(repeat('00', 32), 'hex'), 'revoked', $2, now() - $5, now() - $5 + interval '7 days'
            FROM generate_series(1, $4)
            """,
            organizationId, createdBy, email, count, age);

    private async Task<long> CountInvitationsAsync(Guid organizationId) =>
        (long)(await QuerySingleAsync("SELECT count(*) FROM organization.invitations WHERE org_id = $1", organizationId))[0]!;

    private async Task<IReadOnlyList<(string Type, JsonNode Payload)>> OutboxAsync(Guid organizationId)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand("SELECT event_type, payload FROM organization.outbox WHERE org_id = $1 ORDER BY id", connection);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organizationId });
        await using var reader = await command.ExecuteReaderAsync(CancellationToken);
        var events = new List<(string, JsonNode)>();
        while (await reader.ReadAsync(CancellationToken))
        {
            events.Add((reader.GetString(0), JsonNode.Parse(Encoding.UTF8.GetString(reader.GetFieldValue<byte[]>(1)))!));
        }

        return events;
    }

    [SuppressMessage("Security", "CA2100", Justification = "The SQL is a constant of this test class; values are parameters.")]
    private async Task<object?[]> QuerySingleAsync(string sql, params object[] parameters)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        }

        await using var reader = await command.ExecuteReaderAsync(CancellationToken);
        (await reader.ReadAsync(CancellationToken)).Should().BeTrue();
        var values = new object?[reader.FieldCount];
        for (var index = 0; index < values.Length; index++)
        {
            values[index] = await reader.IsDBNullAsync(index, CancellationToken) ? null : reader.GetValue(index);
        }

        return values;
    }

    [SuppressMessage("Security", "CA2100", Justification = "The SQL is a constant of this test class; values are parameters.")]
    private async Task ExecuteAsync(string sql, params object[] parameters)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        }

        (await command.ExecuteNonQueryAsync(CancellationToken)).Should().BePositive();
    }
}
