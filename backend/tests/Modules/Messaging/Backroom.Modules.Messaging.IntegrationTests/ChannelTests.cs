using System.Net;
using AwesomeAssertions;
using Backroom.Modules.Permissions.Contracts;
using Backroom.Testing;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;

namespace Backroom.Modules.Messaging.IntegrationTests;

/// <summary>
/// Creating and listing public channels (docs/contracts/phase-1.md, flow 4, step 1) with real tokens and real
/// row-level security.
/// </summary>
[Collection(CellCollection.Name)]
[Trait("Category", "Privacy")]
public sealed class ChannelTests(PostgresDatabase database, KeycloakRealm keycloak) : MessagingTestBase(database, keycloak)
{
    [Fact]
    public async Task Create_AsAMember_StoresTheChannelAndAnswersWithItAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);

        using var response = await PostAsync($"/api/v1/orgs/{organizationId}/channels", Alice, new { name = "  General  " });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = (await ReadJsonAsync(response)).AsObject();
        body.Select(property => property.Key).Should().BeEquivalentTo(["id", "name", "createdAt"]);
        body["name"]!.GetValue<string>().Should().Be("General", "the name is trimmed");
        var row = await QuerySingleAsync(
            "SELECT name, org_id, created_by, archived_at IS NULL FROM messaging.channels WHERE id = $1", body["id"]!.GetValue<Guid>());
        row.Should().Equal("General", organizationId, await AccountAsync(Alice), true);
    }

    [Fact]
    public async Task Create_AfterTheCommit_PublishesChannelCreatedAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        long? storedWhenPublished = null;
        Publisher.OnPublish = async () =>
            storedWhenPublished = (long)(await QuerySingleAsync("SELECT count(*) FROM messaging.channels WHERE org_id = $1", organizationId))[0]!;

        using var response = await PostAsync($"/api/v1/orgs/{organizationId}/channels", Alice, new { name = "General" });

        var id = (await ReadJsonAsync(response))["id"]!.GetValue<Guid>();
        storedWhenPublished.Should().Be(1, "live events are sent after the commit, never inside the transaction");
        var published = Publisher.Channels.Should().ContainSingle().Subject;
        published.OrgId.Should().Be(organizationId);
        published.Channel.Id.Should().Be(id);
        published.Channel.Name.Should().Be("General");
    }

    [Fact]
    public async Task Create_WhenPublishingFails_StillSucceedsAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        Publisher.Fail = true;

        using var response = await PostAsync($"/api/v1/orgs/{organizationId}/channels", Alice, new { name = "General" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        (await QuerySingleAsync("SELECT count(*) FROM messaging.channels WHERE org_id = $1", organizationId))[0].Should().Be(1L);
        Logs.Entries.Should().Contain(entry => entry.Contains("Publishing the live event ChannelCreated failed", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("tab\there")]
    [InlineData("line\nbreak")]
    [InlineData("nul\0here")]
    [InlineData("zero\x200Bwidth")]
    [InlineData("\x3164")]
    [InlineData("separator\x2028here")]
    public async Task Create_WithAnInvalidName_IsRefusedAsync(string? name)
    {
        var organizationId = await ProvisionForAsync(Alice);

        using var response = await PostAsync($"/api/v1/orgs/{organizationId}/channels", Alice, new { name });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadJsonAsync(response))["code"]!.GetValue<string>().Should().Be("messaging.channel_name_invalid");
        (await QuerySingleAsync("SELECT count(*) FROM messaging.channels WHERE org_id = $1", organizationId))[0].Should().Be(0L);
    }

    [Fact]
    public async Task Create_WithTheLongestName_SucceedsAndOneMoreIsRefusedAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);

        using var longest = await PostAsync($"/api/v1/orgs/{organizationId}/channels", Alice, new { name = new string('a', 80) });
        using var tooLong = await PostAsync($"/api/v1/orgs/{organizationId}/channels", Alice, new { name = new string('a', 81) });

        longest.StatusCode.Should().Be(HttpStatusCode.Created);
        tooLong.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("tr-TR")]
    public async Task Create_WithANameThatDiffersOnlyByCase_IsAConflictInEveryCultureAsync(string culture)
    {
        using var scope = new CultureScope(culture);
        var organizationId = await ProvisionForAsync(Alice);
        using var first = await PostAsync($"/api/v1/orgs/{organizationId}/channels", Alice, new { name = "Istanbul" });

        using var second = await PostAsync($"/api/v1/orgs/{organizationId}/channels", Alice, new { name = " ISTANBUL " });

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadJsonAsync(second))["code"]!.GetValue<string>().Should().Be("messaging.channel_name_taken");
        (await QuerySingleAsync("SELECT count(*) FROM messaging.channels WHERE org_id = $1", organizationId))[0].Should().Be(1L);
        Publisher.Channels.Should().ContainSingle("the refused request announces nothing");
    }

    [Fact]
    public async Task Create_WithNamesThatDifferOnlyByNormalizationOrNonAsciiCase_IsAConflictAsync()
    {
        using var scope = CultureScope.Turkish();
        var organizationId = await ProvisionForAsync(Alice);

        using var composed = await PostAsync($"/api/v1/orgs/{organizationId}/channels", Alice, new { name = "Café" });
        using var decomposed = await PostAsync($"/api/v1/orgs/{organizationId}/channels", Alice, new { name = "Cafe" + (char)0x301 });
        using var upper = await PostAsync($"/api/v1/orgs/{organizationId}/channels", Alice, new { name = "ÇAĞ" });
        using var lower = await PostAsync($"/api/v1/orgs/{organizationId}/channels", Alice, new { name = "çağ" });

        composed.StatusCode.Should().Be(HttpStatusCode.Created);
        decomposed.StatusCode.Should().Be(HttpStatusCode.Conflict);
        upper.StatusCode.Should().Be(HttpStatusCode.Created);
        lower.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Create_WithTheSameNameInAnotherOrganization_SucceedsAsync()
    {
        var first = await ProvisionForAsync(Alice);
        var second = await ProvisionForAsync(Alice);

        using var one = await PostAsync($"/api/v1/orgs/{first}/channels", Alice, new { name = "General" });
        using var two = await PostAsync($"/api/v1/orgs/{second}/channels", Alice, new { name = "General" });

        one.StatusCode.Should().Be(HttpStatusCode.Created);
        two.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Create_AsAMemberWithoutThePermission_IsForbiddenAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddMemberAsync(organizationId, Bob, role: null);

        using var response = await PostAsync($"/api/v1/orgs/{organizationId}/channels", Bob, new { name = "General" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReadJsonAsync(response))["code"]!.GetValue<string>().Should().Be("permissions.forbidden");
        Publisher.Channels.Should().BeEmpty();
    }

    [Fact]
    public async Task Create_AsAMemberRole_IsAllowedAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddMemberAsync(organizationId, Bob, SystemRoles.Member);

        using var response = await PostAsync($"/api/v1/orgs/{organizationId}/channels", Bob, new { name = "General" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Create_AsSomeoneElse_IsNotFoundRatherThanForbiddenAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);

        using var response = await PostAsync($"/api/v1/orgs/{organizationId}/channels", Bob, new { name = "General" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadJsonAsync(response))["code"]!.GetValue<string>().Should().Be("organization.not_found");
    }

    [Fact]
    public async Task Create_WithoutAToken_IsUnauthorizedAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);

        using var response = await PostAsync($"/api/v1/orgs/{organizationId}/channels", null, new { name = "General" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task List_AsAMember_ReturnsOpenChannelsInCreationOrderAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var first = await CreateChannelAsync(organizationId, Alice, "Alpha");
        var second = await CreateChannelAsync(organizationId, Alice, "Beta");
        var archived = await CreateChannelAsync(organizationId, Alice, "Gamma");
        await ExecuteAsync("UPDATE messaging.channels SET archived_at = now() WHERE id = $1", archived);

        var body = await ReadJsonAsync(await GetAsync($"/api/v1/orgs/{organizationId}/channels", Alice));

        body["channels"]!.AsArray().Select(channel => channel!["id"]!.GetValue<Guid>()).Should().Equal(first, second);
        body["next"].Should().BeNull();
    }

    [Fact]
    public async Task List_ByPages_ReturnsEveryChannelOnceAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        for (var index = 0; index < 5; index++)
        {
            await CreateChannelAsync(organizationId, Alice, $"Channel {index}");
        }

        var seen = new List<Guid>();
        string? after = null;
        var pages = 0;
        do
        {
            var query = after is null ? "limit=2" : $"limit=2&after={after}";
            var body = await ReadJsonAsync(await GetAsync($"/api/v1/orgs/{organizationId}/channels?{query}", Alice));
            seen.AddRange(body["channels"]!.AsArray().Select(channel => channel!["id"]!.GetValue<Guid>()));
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
    [InlineData(-1)]
    public async Task List_WithAPageSizeOutOfRange_IsRefusedAsync(int limit)
    {
        var organizationId = await ProvisionForAsync(Alice);

        using var response = await GetAsync($"/api/v1/orgs/{organizationId}/channels?limit={limit}", Alice);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadJsonAsync(response))["code"]!.GetValue<string>().Should().Be("messaging.page_invalid");
    }

    [Fact]
    public async Task List_WithTheLargestPageSize_IsAcceptedAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);

        using var response = await GetAsync($"/api/v1/orgs/{organizationId}/channels?limit=100", Alice);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task List_ShowsNothingOfAnotherOrganizationAsync()
    {
        var mine = await ProvisionForAsync(Alice);
        var other = await ProvisionForAsync(Alice);
        await CreateChannelAsync(mine, Alice, "Mine");
        await CreateChannelAsync(other, Alice, "Elsewhere");

        var body = await ReadJsonAsync(await GetAsync($"/api/v1/orgs/{mine}/channels", Alice));

        body["channels"]!.AsArray().Should().ContainSingle();
        body.ToJsonString().Should().NotContain("Elsewhere");
    }

    [Fact]
    public async Task List_AsSomeoneElse_IsNotFoundAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await CreateChannelAsync(organizationId, Alice, "Secret plans");

        using var response = await GetAsync($"/api/v1/orgs/{organizationId}/channels", Bob);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync(CancellationToken)).Should().NotContain("Secret plans");
    }

    [Theory]
    [InlineData("after=notaguid")]
    [InlineData("limit=abc")]
    public async Task List_WithAMalformedQuery_OnlyAMemberLearnsItIsBadAsync(string query)
    {
        var organizationId = await ProvisionForAsync(Alice);

        using var member = await GetAsync($"/api/v1/orgs/{organizationId}/channels?{query}", Alice);
        using var someoneElse = await GetAsync($"/api/v1/orgs/{organizationId}/channels?{query}", Bob);

        member.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        someoneElse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
