using System.Net;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Backroom.Modules.Messaging.Contracts;
using Backroom.Modules.Permissions.Contracts;
using Backroom.Modules.Realtime.Contracts;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;

namespace Backroom.Modules.Messaging.IntegrationTests;

/// <summary>
/// Private channels (docs/contracts/phase-2.md, flow 1, step 7; "Privacy rules and their tests"): only members see a
/// private channel's name, members and content, whatever their role in the organization; the admins' whole view is one
/// number; names reveal nothing; membership changes, ownership and removal, with real tokens and real row-level
/// security.
/// </summary>
[Collection(CellCollection.Name)]
[Trait("Category", "Privacy")]
public sealed class PrivateChannelTests(PostgresDatabase database, KeycloakRealm keycloak) : MessagingTestBase(database, keycloak)
{
    private const string SecretName = "Zebra-Merger-Plans";

    [Fact]
    public async Task CreatePrivateChannel_MakesTheCreatorOwnerAndTellsOnlyItsMembersAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddMemberAsync(organizationId, Bob, SystemRoles.Member);
        var alice = await AccountAsync(Alice);
        var bob = await AccountAsync(Bob);

        using var response = await PostAsync(
            $"/api/v1/orgs/{organizationId}/channels", Alice,
            new { name = SecretName, @private = true, description = "Quiet", memberAccountIds = new[] { bob, alice, bob } });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await ReadJsonAsync(response);
        var channelId = body["id"]!.GetValue<Guid>();
        body["kind"]!.GetValue<string>().Should().Be("private_channel");
        body["description"]!.GetValue<string>().Should().Be("Quiet");
        (await QuerySingleAsync("SELECT kind, owner_account_id, member_count, description FROM messaging.conversations WHERE id = $1", channelId))
            .Should().Equal("private_channel", alice, 2, "Quiet");
        (await QuerySingleAsync("SELECT role, added_by FROM messaging.conversation_members WHERE conversation_id = $1 AND account_id = $2", channelId, alice))
            .Should().Equal("owner", alice);
        (await QuerySingleAsync("SELECT role, added_by, joined_seq FROM messaging.conversation_members WHERE conversation_id = $1 AND account_id = $2", channelId, bob))
            .Should().Equal("member", alice, 0L);

        var events = await OutboxAsync(organizationId);
        events.Should().ContainSingle(entry => entry.Type == ConversationCreated.EventType).Which.Payload["kind"]!.GetValue<string>().Should().Be("private_channel");
        events.Where(entry => entry.Type == ConversationMemberAdded.EventType).Select(entry => entry.Payload["account_id"]!.GetValue<Guid>())
            .Should().BeEquivalentTo([alice, bob]);
        events.Select(entry => entry.Payload.ToJsonString()).Should().NotContain(payload => payload.Contains(SecretName, StringComparison.Ordinal), "events carry ids only");

        Publisher.Channels.Should().BeEmpty("the organization is told about public channels only");
        Publisher.Memberships.Should().BeEquivalentTo(
        [
            new MembershipChangedPayload(organizationId, alice, channelId, MembershipChangedPayload.Added),
            new MembershipChangedPayload(organizationId, bob, channelId, MembershipChangedPayload.Added),
        ]);
        Logs.Entries.Should().NotContain(entry => entry.Contains(SecretName, StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreatePrivateChannel_NeedsItsOwnPermissionAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddMemberAsync(organizationId, Bob, null);

        using var refused = await PostAsync($"/api/v1/orgs/{organizationId}/channels", Bob, new { name = "Mine", @private = true });

        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorCodeAsync(refused)).Should().Be("permissions.forbidden");
        (await QuerySingleAsync("SELECT count(*) FROM messaging.conversations WHERE org_id = $1", organizationId)).Should().Equal(0L);

        var memberOrganization = await ProvisionForAsync(Alice);
        await AddMemberAsync(memberOrganization, Bob, SystemRoles.Member);
        using var allowed = await PostAsync($"/api/v1/orgs/{memberOrganization}/channels", Bob, new { name = "Mine", @private = true });
        allowed.StatusCode.Should().Be(HttpStatusCode.Created, "the Member role may create private channels");
    }

    [Fact]
    public async Task CreatePrivateChannel_WithAnyNameInUse_SucceedsAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddMemberAsync(organizationId, Bob, SystemRoles.Member);
        await CreateChannelAsync(organizationId, Alice, "Design");

        await CreatePrivateChannelAsync(organizationId, Bob, "Design");
        await CreatePrivateChannelAsync(organizationId, Alice, "DESIGN");
        await CreatePrivateChannelAsync(organizationId, Alice, "design");

        (await QuerySingleAsync("SELECT count(*) FROM messaging.conversations WHERE org_id = $1 AND name_key = 'design'", organizationId)).Should().Equal(4L);
    }

    [Fact]
    public async Task CreateChannel_WithSomeoneOutsideTheOrganization_StoresNothingAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);

        using var response = await PostAsync(
            $"/api/v1/orgs/{organizationId}/channels", Alice, new { name = "Mine", @private = true, memberAccountIds = new[] { Guid.CreateVersion7() } });
        using var tooMany = await PostAsync(
            $"/api/v1/orgs/{organizationId}/channels", Alice,
            new { name = "Mine", @private = true, memberAccountIds = Enumerable.Range(0, 51).Select(_ => Guid.CreateVersion7()).ToArray() });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCodeAsync(response)).Should().Be("messaging.member_not_in_organization");
        tooMany.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCodeAsync(tooMany)).Should().Be("messaging.members_invalid");
        (await QuerySingleAsync("SELECT count(*) FROM messaging.conversations WHERE org_id = $1", organizationId)).Should().Equal(0L);
        (await OutboxAsync(organizationId)).Should().BeEmpty();
        Publisher.Memberships.Should().BeEmpty();
    }

    /// <summary>
    /// "No role reads others' messages": the Owner role and the Admin role, without membership, get the answer of a
    /// conversation that does not exist from every route, and the channel is in none of their lists.
    /// </summary>
    [Theory]
    [InlineData(SystemRoles.Owner, false)]
    [InlineData(SystemRoles.Admin, false)]
    [InlineData(SystemRoles.Owner, true)]
    [InlineData(SystemRoles.Admin, true)]
    public async Task PrivateChannel_ForOwnerAndAdminWithoutMembership_IsNotFoundOnEveryRouteAsync(string outsiderRole, bool archived)
    {
        // Alice provisions the organization, so she holds the Owner role; Bob gets the role under test or creates.
        var organizationId = await ProvisionForAsync(Alice);
        var (creator, outsider) = outsiderRole == SystemRoles.Owner ? (Bob, Alice) : (Alice, Bob);
        await AddMemberAsync(organizationId, Bob, outsiderRole == SystemRoles.Owner ? SystemRoles.Member : SystemRoles.Admin);
        var creatorId = await AccountAsync(creator);
        var outsiderId = await AccountAsync(outsider);
        var channelId = await CreatePrivateChannelAsync(organizationId, creator, SecretName);
        await SendMessageAsync(organizationId, channelId, creator, "for members only");
        if (archived)
        {
            // Archiving makes a conversation read-only for its members; it opens it to nobody else.
            (await SendAsync(HttpMethod.Post, $"/api/v1/orgs/{organizationId}/conversations/{channelId}/archive", creator, null)).EnsureSuccessStatusCode();
        }

        var updates = Publisher.Conversations.Count;
        var unknown = Guid.CreateVersion7();
        var before = await QuerySingleAsync("SELECT member_count, name, owner_account_id, archived_at IS NULL, change_seq FROM messaging.conversations WHERE id = $1", channelId);

        (HttpMethod Method, string Path, object? Body)[] Routes(Guid id) =>
        [
            (HttpMethod.Get, $"conversations/{id}", null),
            (HttpMethod.Patch, $"conversations/{id}", new { name = "Taken over", description = "x" }),
            (HttpMethod.Post, $"conversations/{id}/archive", null),
            (HttpMethod.Post, $"conversations/{id}/unarchive", null),
            (HttpMethod.Post, $"conversations/{id}/join", null),
            (HttpMethod.Post, $"conversations/{id}/leave", null),
            (HttpMethod.Get, $"conversations/{id}/members", null),
            (HttpMethod.Post, $"conversations/{id}/members", new { accountIds = new[] { outsiderId } }),
            (HttpMethod.Delete, $"conversations/{id}/members/{creatorId}", null),
            (HttpMethod.Delete, $"conversations/{id}/members/{outsiderId}", null),
            (HttpMethod.Put, $"conversations/{id}/owner", new { accountId = outsiderId }),
            (HttpMethod.Get, $"conversations/{id}/messages", null),
            (HttpMethod.Post, $"conversations/{id}/messages", new { clientMessageId = Guid.NewGuid(), text = "let me in" }),
        ];

        foreach (var ((method, path, body), (_, unknownPath, unknownBody)) in Routes(channelId).Zip(Routes(unknown)))
        {
            using var response = await SendAsync(method, $"/api/v1/orgs/{organizationId}/{path}", outsider, body);
            using var missing = await SendAsync(method, $"/api/v1/orgs/{organizationId}/{unknownPath}", outsider, unknownBody);

            response.StatusCode.Should().Be(HttpStatusCode.NotFound, $"{method} {path}");
            var answer = await ReadJsonAsync(response);
            var answerToUnknown = await ReadJsonAsync(missing);
            answer["code"]!.GetValue<string>().Should().Be("messaging.conversation_not_found", $"{method} {path}");
            answer.ToJsonString().Should().NotContain(SecretName);
            // The trace id differs per request; everything else is the same.
            answer.AsObject().Remove("traceId");
            answerToUnknown.AsObject().Remove("traceId");
            answer.ToJsonString().Replace(channelId.ToString(), "{id}", StringComparison.Ordinal)
                .Should().Be(answerToUnknown.ToJsonString().Replace(unknown.ToString(), "{id}", StringComparison.Ordinal),
                    "a private channel answers exactly like a conversation that does not exist");
        }

        using var conversations = await GetAsync($"/api/v1/orgs/{organizationId}/conversations", outsider);
        using var channels = await GetAsync($"/api/v1/orgs/{organizationId}/channels", outsider);
        (await conversations.Content.ReadAsStringAsync(CancellationToken)).Should().NotContain(SecretName).And.NotContain(channelId.ToString());
        (await channels.Content.ReadAsStringAsync(CancellationToken)).Should().NotContain(SecretName).And.NotContain(channelId.ToString());

        (await QuerySingleAsync("SELECT member_count, name, owner_account_id, archived_at IS NULL, change_seq FROM messaging.conversations WHERE id = $1", channelId))
            .Should().Equal(before, "nothing an outsider sends changes the channel");
        Publisher.Memberships.Should().OnlyContain(payload => payload.AccountId == creatorId);
        Publisher.Conversations.Should().HaveCount(updates);
        Publisher.Channels.Should().BeEmpty();

        var canRead = true;
        await InOrganizationAsync(organizationId, async services =>
            canRead = await services.GetRequiredService<IConversationReadAccess>().CanReadAsync(organizationId, channelId, outsiderId, CancellationToken));
        canRead.Should().BeFalse("files, live subscriptions and notifications ask this");
    }

    [Fact]
    public async Task PrivateChannelCount_WhenAdmin_ReturnsOnlyTotalAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddMemberAsync(organizationId, Bob, SystemRoles.Admin);
        var otherOrganization = await ProvisionForAsync(Alice);
        await CreateChannelAsync(organizationId, Alice, "General");
        await CreatePrivateChannelAsync(organizationId, Alice, SecretName);
        await CreatePrivateChannelAsync(organizationId, Alice, SecretName);
        var archived = await CreatePrivateChannelAsync(organizationId, Alice, "Closed");
        (await SendAsync(HttpMethod.Post, $"/api/v1/orgs/{organizationId}/conversations/{archived}/archive", Alice, null)).EnsureSuccessStatusCode();
        await CreatePrivateChannelAsync(otherOrganization, Alice, "Elsewhere");

        using var response = await GetAsync($"/api/v1/orgs/{organizationId}/private-channels/count", Bob);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await ReadJsonAsync(response)).AsObject();
        body.Select(property => property.Key).Should().Equal(["count"], "a number and nothing else: no ids, names or member counts");
        body["count"]!.GetValue<int>().Should().Be(2, "open private channels of this organization");
    }

    [Fact]
    public async Task PrivateChannelCount_WhenMember_IsForbiddenAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddMemberAsync(organizationId, Bob, SystemRoles.Member);
        await CreatePrivateChannelAsync(organizationId, Bob, SecretName);

        using var response = await GetAsync($"/api/v1/orgs/{organizationId}/private-channels/count", Bob);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorCodeAsync(response)).Should().Be("permissions.forbidden");
    }

    [Fact]
    public async Task RenameChannel_WhenNameMatchesPrivateChannelOfOthers_SucceedsAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddMemberAsync(organizationId, Bob, SystemRoles.Member);
        await CreatePrivateChannelAsync(organizationId, Bob, SecretName);
        var publicId = await CreateChannelAsync(organizationId, Alice, "General");
        var ownPrivate = await CreatePrivateChannelAsync(organizationId, Alice, "Mine");

        // A public target and a private target: neither answer depends on Bob's private channel.
        using var publicRename = await SendAsync(HttpMethod.Patch, $"/api/v1/orgs/{organizationId}/conversations/{publicId}", Alice, new { name = SecretName.ToUpperInvariant() });
        using var privateRename = await SendAsync(HttpMethod.Patch, $"/api/v1/orgs/{organizationId}/conversations/{ownPrivate}", Alice, new { name = SecretName });
        // A private channel may also take a public channel's name.
        using var asPublic = await SendAsync(HttpMethod.Patch, $"/api/v1/orgs/{organizationId}/conversations/{ownPrivate}", Alice, new { name = SecretName.ToUpperInvariant() });

        publicRename.StatusCode.Should().Be(HttpStatusCode.OK);
        privateRename.StatusCode.Should().Be(HttpStatusCode.OK);
        asPublic.StatusCode.Should().Be(HttpStatusCode.OK);
        (await QuerySingleAsync("SELECT count(*) FROM messaging.conversations WHERE org_id = $1 AND name_key = lower($2)", organizationId, SecretName)).Should().Equal(3L);
    }

    [Fact]
    public async Task Rename_APrivateChannel_ByItsOwner_TellsItsGroupAndWritesNoNameAnywhereElseAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddMemberAsync(organizationId, Bob, SystemRoles.Admin);
        var alice = await AccountAsync(Alice);
        var bob = await AccountAsync(Bob);
        var channelId = await CreatePrivateChannelAsync(organizationId, Alice, "Before", bob);

        // Bob is a member and an Admin, but not the channel's owner.
        using var refused = await SendAsync(HttpMethod.Patch, $"/api/v1/orgs/{organizationId}/conversations/{channelId}", Bob, new { name = "Bob's" });
        using var response = await SendAsync(
            HttpMethod.Patch, $"/api/v1/orgs/{organizationId}/conversations/{channelId}", Alice, new { name = SecretName, description = "  What we plan  " });

        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorCodeAsync(refused)).Should().Be("messaging.not_conversation_owner");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadJsonAsync(response);
        body["name"]!.GetValue<string>().Should().Be(SecretName);
        body["description"]!.GetValue<string>().Should().Be("What we plan");
        (await QuerySingleAsync("SELECT name, name_key, description, change_seq FROM messaging.conversations WHERE id = $1", channelId))
            .Should().Equal(SecretName, SecretName.ToLowerInvariant(), "What we plan", 1L);

        var changed = (await OutboxAsync(organizationId)).Should().ContainSingle(entry => entry.Type == ConversationChanged.EventType).Subject.Payload.AsObject();
        changed.Select(property => property.Key).Should().BeEquivalentTo(
            ["event_id", "org_id", "occurred_at", "conversation_id", "kind", "change", "actor_account_id", "member_count"]);
        changed["change"]!.GetValue<string>().Should().Be("renamed");
        changed["kind"]!.GetValue<string>().Should().Be("private_channel");
        changed["actor_account_id"]!.GetValue<Guid>().Should().Be(alice);
        changed["member_count"]!.GetValue<int>().Should().Be(2);
        (await OutboxAsync(organizationId)).Select(entry => entry.Payload.ToJsonString())
            .Should().NotContain(payload => payload.Contains(SecretName, StringComparison.Ordinal) || payload.Contains("What we plan", StringComparison.Ordinal));
        Logs.Entries.Should().NotContain(entry => entry.Contains(SecretName, StringComparison.Ordinal) || entry.Contains("What we plan", StringComparison.Ordinal));

        // The name travels only in the event for the conversation's own group, which only members can join.
        Publisher.Conversations.Should().Equal(
            new ConversationUpdatedPayload(organizationId, channelId, SecretName, "What we plan", null, 2, 1, TestCellEpoch.Initial.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Publisher.Channels.Should().BeEmpty();
    }

    [Fact]
    public async Task GetConversationAndMembers_ForAMember_ShowTheChannelAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddMemberAsync(organizationId, Bob, SystemRoles.Member);
        var alice = await AccountAsync(Alice);
        var bob = await AccountAsync(Bob);
        var silent = await AddSilentMemberAsync(organizationId);
        var channelId = await CreatePrivateChannelAsync(organizationId, Alice, SecretName, bob, silent);

        using var conversation = await GetAsync($"/api/v1/orgs/{organizationId}/conversations/{channelId}", Bob);
        using var firstPage = await GetAsync($"/api/v1/orgs/{organizationId}/conversations/{channelId}/members?limit=2", Bob);

        conversation.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadJsonAsync(conversation);
        body["kind"]!.GetValue<string>().Should().Be("private_channel");
        body["name"]!.GetValue<string>().Should().Be(SecretName);
        body["memberCount"]!.GetValue<int>().Should().Be(3);
        body["ownerAccountId"]!.GetValue<Guid>().Should().Be(alice);
        body["role"]!.GetValue<string>().Should().Be("member");
        body["archivedAt"].Should().BeNull();

        var page = await ReadJsonAsync(firstPage);
        var next = page["next"]!.GetValue<string>();
        using var secondPage = await GetAsync($"/api/v1/orgs/{organizationId}/conversations/{channelId}/members?limit=2&after={next}", Bob);
        var rest = await ReadJsonAsync(secondPage);
        rest["next"].Should().BeNull();
        var members = page["items"]!.AsArray().Concat(rest["items"]!.AsArray()).ToArray();
        members.Select(member => member!["accountId"]!.GetValue<Guid>()).Should().BeEquivalentTo([alice, bob, silent]);
        members.Single(member => member!["accountId"]!.GetValue<Guid>() == alice)!["role"]!.GetValue<string>().Should().Be("owner");

        using var badPage = await GetAsync($"/api/v1/orgs/{organizationId}/conversations/{channelId}/members?limit=101", Bob);
        badPage.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AddMembers_ByAnyMember_AddsActiveOrganizationMembersOnceAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddMemberAsync(organizationId, Bob, null);
        var bob = await AccountAsync(Bob);
        var first = await AddSilentMemberAsync(organizationId);
        var second = await AddSilentMemberAsync(organizationId);
        var channelId = await CreatePrivateChannelAsync(organizationId, Alice, SecretName, bob);
        await SendMessageAsync(organizationId, channelId, Alice, "one");
        var path = $"/api/v1/orgs/{organizationId}/conversations/{channelId}/members";

        // Bob holds no role at all: adding belongs to membership of the channel.
        using var added = await PostAsync(path, Bob, new { accountIds = new[] { first, bob } });
        using var again = await PostAsync(path, Bob, new { accountIds = new[] { first, second } });

        added.StatusCode.Should().Be(HttpStatusCode.NoContent);
        again.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await QuerySingleAsync("SELECT member_count FROM messaging.conversations WHERE id = $1", channelId)).Should().Equal(4);
        (await QuerySingleAsync("SELECT role, added_by, joined_seq FROM messaging.conversation_members WHERE conversation_id = $1 AND account_id = $2", channelId, first))
            .Should().Equal("member", bob, 1L);
        var events = (await OutboxAsync(organizationId)).Where(entry => entry.Type == ConversationMemberAdded.EventType && entry.Payload["actor_account_id"]!.GetValue<Guid>() == bob).ToArray();
        events.Select(entry => entry.Payload["account_id"]!.GetValue<Guid>()).Should().BeEquivalentTo([first, second], "each person is announced once");
        Publisher.Memberships.Where(payload => payload.AccountId == first || payload.AccountId == second).Should().BeEquivalentTo(
        [
            new MembershipChangedPayload(organizationId, first, channelId, MembershipChangedPayload.Added),
            new MembershipChangedPayload(organizationId, second, channelId, MembershipChangedPayload.Added),
        ]);
    }

    [Fact]
    public async Task AddMembers_WithABadList_StoresNothingAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var silent = await AddSilentMemberAsync(organizationId);
        var channelId = await CreatePrivateChannelAsync(organizationId, Alice, SecretName);
        var path = $"/api/v1/orgs/{organizationId}/conversations/{channelId}/members";

        using var outsider = await PostAsync(path, Alice, new { accountIds = new[] { silent, Guid.CreateVersion7() } });
        using var empty = await PostAsync(path, Alice, new { accountIds = Array.Empty<Guid>() });
        using var missing = await PostAsync(path, Alice, new { });
        using var tooMany = await PostAsync(path, Alice, new { accountIds = Enumerable.Range(0, 51).Select(_ => Guid.CreateVersion7()).ToArray() });

        outsider.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCodeAsync(outsider)).Should().Be("messaging.member_not_in_organization");
        foreach (var response in new[] { empty, missing, tooMany })
        {
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await ErrorCodeAsync(response)).Should().Be("messaging.members_invalid");
        }

        (await QuerySingleAsync("SELECT member_count FROM messaging.conversations WHERE id = $1", channelId)).Should().Equal(1);
    }

    [Fact]
    public async Task AddMembers_ToOtherKinds_IsRefusedAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var alice = await AccountAsync(Alice);
        var silent = await AddSilentMemberAsync(organizationId);
        var publicId = await CreateChannelAsync(organizationId, Alice, "General");
        var groupDm = Guid.CreateVersion7();
        await ExecuteAsync(
            "INSERT INTO messaging.conversations (id, org_id, kind, created_by, created_at, member_count) VALUES ($1, $2, 'group_dm', $3, now(), 1)",
            groupDm, organizationId, alice);
        await ExecuteAsync(
            "INSERT INTO messaging.conversation_members (org_id, conversation_id, account_id, role, joined_at, joined_seq, added_by) VALUES ($1, $2, $3, 'member', now(), 0, $3)",
            organizationId, groupDm, alice);

        using var toPublic = await PostAsync($"/api/v1/orgs/{organizationId}/conversations/{publicId}/members", Alice, new { accountIds = new[] { silent } });
        using var toGroup = await PostAsync($"/api/v1/orgs/{organizationId}/conversations/{groupDm}/members", Alice, new { accountIds = new[] { silent } });
        using var removeFromPublic = await SendAsync(HttpMethod.Delete, $"/api/v1/orgs/{organizationId}/conversations/{publicId}/members/{silent}", Alice, null);
        using var ownerOfPublic = await SendAsync(HttpMethod.Put, $"/api/v1/orgs/{organizationId}/conversations/{publicId}/owner", Alice, new { accountId = silent });

        toPublic.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ErrorCodeAsync(toPublic)).Should().Be("messaging.not_a_private_channel");
        toGroup.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ErrorCodeAsync(toGroup)).Should().Be("messaging.group_dm_add_creates_new");
        (await ErrorCodeAsync(removeFromPublic)).Should().Be("messaging.not_a_private_channel");
        (await ErrorCodeAsync(ownerOfPublic)).Should().Be("messaging.not_a_private_channel");
    }

    [Fact]
    public async Task RemoveMember_ByTheOwner_EndsAccessAtOnceAndLeavesTheLiveGroupAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddMemberAsync(organizationId, Bob, SystemRoles.Admin);
        var alice = await AccountAsync(Alice);
        var bob = await AccountAsync(Bob);
        var channelId = await CreatePrivateChannelAsync(organizationId, Alice, SecretName, bob);
        await SendMessageAsync(organizationId, channelId, Alice, "before");
        (await GetAsync($"/api/v1/orgs/{organizationId}/conversations/{channelId}/messages", Bob)).StatusCode.Should().Be(HttpStatusCode.OK);
        var committedAtBroadcast = false;
        Publisher.OnPublish = async () => committedAtBroadcast |=
            (bool)(await QuerySingleAsync("SELECT left_at IS NOT NULL FROM messaging.conversation_members WHERE conversation_id = $1 AND account_id = $2", channelId, bob))[0]!;

        using var response = await SendAsync(HttpMethod.Delete, $"/api/v1/orgs/{organizationId}/conversations/{channelId}/members/{bob}", Alice, null);
        Publisher.OnPublish = null;

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        committedAtBroadcast.Should().BeTrue("the removal is broadcast after its commit");
        Publisher.Removals.Should().Equal([(organizationId, channelId, bob)], "the removed person's connections leave the conversation's live group");
        Publisher.Memberships.Should().Contain(new MembershipChangedPayload(organizationId, bob, channelId, MembershipChangedPayload.Removed));
        var removed = (await OutboxAsync(organizationId)).Should().ContainSingle(entry => entry.Type == ConversationMemberRemoved.EventType).Subject.Payload;
        removed["account_id"]!.GetValue<Guid>().Should().Be(bob);
        removed["actor_account_id"]!.GetValue<Guid>().Should().Be(alice);
        (await QuerySingleAsync("SELECT member_count FROM messaging.conversations WHERE id = $1", channelId)).Should().Equal(1);

        // The next request is refused, with the same token, an Admin role and all.
        foreach (var path in new[] { "", "/messages", "/members" })
        {
            using var read = await GetAsync($"/api/v1/orgs/{organizationId}/conversations/{channelId}{path}", Bob);
            read.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        var canRead = true;
        await InOrganizationAsync(organizationId, async services =>
            canRead = await services.GetRequiredService<IConversationReadAccess>().CanReadAsync(organizationId, channelId, bob, CancellationToken));
        canRead.Should().BeFalse("Realtime's subscription check refuses a new subscription");

        using var again = await SendAsync(HttpMethod.Delete, $"/api/v1/orgs/{organizationId}/conversations/{channelId}/members/{bob}", Alice, null);
        again.StatusCode.Should().Be(HttpStatusCode.NoContent, "removing someone who is not a member changes nothing");
        Publisher.Removals.Should().HaveCount(1);
    }

    [Fact]
    public async Task RemoveMember_ByAnotherMember_IsForbiddenAndRemovingOneselfIsLeavingAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddMemberAsync(organizationId, Bob, SystemRoles.Admin);
        var alice = await AccountAsync(Alice);
        var bob = await AccountAsync(Bob);
        var channelId = await CreatePrivateChannelAsync(organizationId, Alice, SecretName, bob);

        using var refused = await SendAsync(HttpMethod.Delete, $"/api/v1/orgs/{organizationId}/conversations/{channelId}/members/{alice}", Bob, null);
        using var left = await SendAsync(HttpMethod.Delete, $"/api/v1/orgs/{organizationId}/conversations/{channelId}/members/{bob}", Bob, null);

        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorCodeAsync(refused)).Should().Be("messaging.not_conversation_owner");
        left.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await QuerySingleAsync("SELECT member_count, owner_account_id FROM messaging.conversations WHERE id = $1", channelId)).Should().Equal(1, alice);
        Publisher.Removals.Should().Equal([(organizationId, channelId, bob)]);
    }

    [Fact]
    public async Task TransferOwnership_ByTheOwner_MovesTheOwnerRightsAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddMemberAsync(organizationId, Bob, SystemRoles.Member);
        var alice = await AccountAsync(Alice);
        var bob = await AccountAsync(Bob);
        var silent = await AddSilentMemberAsync(organizationId);
        var channelId = await CreatePrivateChannelAsync(organizationId, Alice, SecretName, bob);
        var path = $"/api/v1/orgs/{organizationId}/conversations/{channelId}/owner";

        using var byMember = await SendAsync(HttpMethod.Put, path, Bob, new { accountId = bob });
        using var toOutsider = await SendAsync(HttpMethod.Put, path, Alice, new { accountId = silent });
        using var toNobody = await SendAsync(HttpMethod.Put, path, Alice, new { });
        using var response = await SendAsync(HttpMethod.Put, path, Alice, new { accountId = bob });

        byMember.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorCodeAsync(byMember)).Should().Be("messaging.not_conversation_owner");
        toOutsider.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ErrorCodeAsync(toOutsider)).Should().Be("messaging.owner_must_be_member");
        toNobody.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await QuerySingleAsync("SELECT owner_account_id FROM messaging.conversations WHERE id = $1", channelId)).Should().Equal(bob);
        (await QuerySingleAsync(
            "SELECT string_agg(role, ',' ORDER BY role) FROM messaging.conversation_members WHERE conversation_id = $1 AND account_id = ANY($2)",
            channelId, new[] { alice, bob })).Should().Equal("member,owner");
        (await QuerySingleAsync("SELECT role FROM messaging.conversation_members WHERE conversation_id = $1 AND account_id = $2", channelId, bob)).Should().Equal("owner");
        var changed = (await OutboxAsync(organizationId)).Should().ContainSingle(entry => entry.Type == ConversationChanged.EventType).Subject.Payload;
        changed["change"]!.GetValue<string>().Should().Be("owner_transferred");
        changed["actor_account_id"]!.GetValue<Guid>().Should().Be(alice);
        // Clients learn of it: the change takes a change number and the conversation's group is told.
        (await QuerySingleAsync("SELECT change_seq FROM messaging.conversations WHERE id = $1", channelId)).Should().Equal(1L);
        Publisher.Conversations.Should().ContainSingle().Which.ChangeSeq.Should().Be(1);

        using var oldOwner = await SendAsync(HttpMethod.Post, $"/api/v1/orgs/{organizationId}/conversations/{channelId}/archive", Alice, null);
        using var newOwner = await SendAsync(HttpMethod.Post, $"/api/v1/orgs/{organizationId}/conversations/{channelId}/archive", Bob, null);
        oldOwner.StatusCode.Should().Be(HttpStatusCode.Forbidden, "the Owner role of the organization does not own the channel");
        newOwner.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Leave_ByTheOwner_PassesOwnershipToTheEarliestMemberThenTheLowestAccountIdAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddMemberAsync(organizationId, Bob, SystemRoles.Member);
        var alice = await AccountAsync(Alice);
        var bob = await AccountAsync(Bob);
        var first = await AddSilentMemberAsync(organizationId);
        var second = await AddSilentMemberAsync(organizationId);
        // The two silent members join in one statement, at the same time; Bob joins later.
        var channelId = await CreatePrivateChannelAsync(organizationId, Alice, SecretName, first, second);
        (await PostAsync($"/api/v1/orgs/{organizationId}/conversations/{channelId}/members", Alice, new { accountIds = new[] { bob } })).EnsureSuccessStatusCode();
        var lowest = string.CompareOrdinal(first.ToString("N"), second.ToString("N")) < 0 ? first : second;

        using var left = await SendAsync(HttpMethod.Post, $"/api/v1/orgs/{organizationId}/conversations/{channelId}/leave", Alice, null);

        left.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await QuerySingleAsync("SELECT owner_account_id, member_count, archived_at IS NULL FROM messaging.conversations WHERE id = $1", channelId))
            .Should().Equal(lowest, 3, true);
        (await QuerySingleAsync(
            "SELECT count(*) FILTER (WHERE role = 'owner'), bool_or(role = 'owner' AND account_id = $2) FROM messaging.conversation_members WHERE conversation_id = $1",
            channelId, lowest)).Should().Equal(1L, true);
        var changed = (await OutboxAsync(organizationId)).Should().ContainSingle(entry => entry.Type == ConversationChanged.EventType).Subject.Payload;
        changed["change"]!.GetValue<string>().Should().Be("owner_transferred");
        changed["actor_account_id"]!.GetValue<Guid>().Should().Be(alice);
        changed["member_count"]!.GetValue<int>().Should().Be(3);
        Publisher.Conversations.Should().HaveCount(2, "Bob was added, then the owner left").And.Subject.Last().MemberCount.Should().Be(3);
        (await GetAsync($"/api/v1/orgs/{organizationId}/conversations/{channelId}", Alice)).StatusCode.Should().Be(HttpStatusCode.NotFound, "the former owner is no member");
    }

    [Fact]
    public async Task Leave_ByTheLastMember_ArchivesThePrivateChannelAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreatePrivateChannelAsync(organizationId, Alice, SecretName);

        using var left = await SendAsync(HttpMethod.Post, $"/api/v1/orgs/{organizationId}/conversations/{channelId}/leave", Alice, null);

        left.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await QuerySingleAsync("SELECT member_count, archived_at IS NOT NULL FROM messaging.conversations WHERE id = $1", channelId)).Should().Equal(0, true);
        (await OutboxAsync(organizationId)).Should().ContainSingle(entry => entry.Type == ConversationChanged.EventType)
            .Which.Payload["change"]!.GetValue<string>().Should().Be("archived");
        using var count = await GetAsync($"/api/v1/orgs/{organizationId}/private-channels/count", Alice);
        (await ReadJsonAsync(count))["count"]!.GetValue<int>().Should().Be(0);
    }

    [Fact]
    public async Task Archive_APrivateChannel_ByItsOwner_ClosesItForChangesUntilReopenedAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddMemberAsync(organizationId, Bob, SystemRoles.Admin);
        var bob = await AccountAsync(Bob);
        var silent = await AddSilentMemberAsync(organizationId);
        var channelId = await CreatePrivateChannelAsync(organizationId, Alice, SecretName, bob);
        var basePath = $"/api/v1/orgs/{organizationId}/conversations/{channelId}";

        using var byMember = await SendAsync(HttpMethod.Post, $"{basePath}/archive", Bob, null);
        using var archived = await SendAsync(HttpMethod.Post, $"{basePath}/archive", Alice, null);
        using var again = await SendAsync(HttpMethod.Post, $"{basePath}/archive", Alice, null);

        byMember.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        archived.StatusCode.Should().Be(HttpStatusCode.NoContent);
        again.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await QuerySingleAsync("SELECT archived_at IS NOT NULL, change_seq FROM messaging.conversations WHERE id = $1", channelId)).Should().Equal(true, 1L);
        Publisher.Conversations.Should().ContainSingle().Which.ArchivedAt.Should().NotBeNull();
        (await OutboxAsync(organizationId)).Where(entry => entry.Type == ConversationChanged.EventType)
            .Select(entry => entry.Payload["change"]!.GetValue<string>()).Should().Equal(["archived"]);

        using var rename = await SendAsync(HttpMethod.Patch, basePath, Alice, new { name = "New" });
        using var add = await PostAsync($"{basePath}/members", Alice, new { accountIds = new[] { silent } });
        using var details = await GetAsync(basePath, Bob);
        (await ErrorCodeAsync(rename)).Should().Be("messaging.conversation_archived");
        (await ErrorCodeAsync(add)).Should().Be("messaging.conversation_archived");
        (await ReadJsonAsync(details))["archivedAt"].Should().NotBeNull();

        // Readable by its members, and read-only.
        using var history = await GetAsync($"{basePath}/messages", Bob);
        using var memberList = await GetAsync($"{basePath}/members", Bob);
        using var send = await PostAsync($"{basePath}/messages", Bob, new { clientMessageId = Guid.NewGuid(), text = "too late" });
        history.StatusCode.Should().Be(HttpStatusCode.OK);
        memberList.StatusCode.Should().Be(HttpStatusCode.OK);
        send.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ErrorCodeAsync(send)).Should().Be("messaging.conversation_archived");

        using var reopened = await SendAsync(HttpMethod.Post, $"{basePath}/unarchive", Alice, null);
        reopened.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await QuerySingleAsync("SELECT archived_at IS NULL, change_seq FROM messaging.conversations WHERE id = $1", channelId)).Should().Equal(true, 2L);
        Publisher.Conversations.Should().HaveCount(2);
        Publisher.Conversations[1].ArchivedAt.Should().BeNull();
        await SendMessageAsync(organizationId, channelId, Bob, "open again");
    }

    [Fact]
    public async Task Brakes_OnNewChannelsAndMemberAdditions_AnswerTooManyRequestsAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var alice = await AccountAsync(Alice);
        var silent = await AddSilentMemberAsync(organizationId);
        var channelId = await CreatePrivateChannelAsync(organizationId, Alice, SecretName);
        await ExecuteAsync(
            """
            INSERT INTO messaging.conversation_members (org_id, conversation_id, account_id, role, joined_at, joined_seq, added_by, left_at)
            SELECT $1, $2, gen_random_uuid(), 'member', now() - interval '10 minutes', 0, $3, now() FROM generate_series(1, 200)
            """,
            organizationId, channelId, alice);

        using var add = await PostAsync($"/api/v1/orgs/{organizationId}/conversations/{channelId}/members", Alice, new { accountIds = new[] { silent } });

        add.StatusCode.Should().Be((HttpStatusCode)429);
        (await ErrorCodeAsync(add)).Should().Be("messaging.rate_limited");
        add.Headers.RetryAfter!.Delta!.Value.Should().BeGreaterThan(TimeSpan.FromMinutes(45)).And.BeLessThanOrEqualTo(TimeSpan.FromMinutes(50));

        await ExecuteAsync(
            """
            INSERT INTO messaging.conversations (id, org_id, kind, name, name_key, created_by, created_at)
            SELECT gen_random_uuid(), $1, 'public_channel', 'c' || n, 'c' || n, $2, now() - interval '30 minutes' FROM generate_series(1, 19) AS n
            """,
            organizationId, alice);
        using var create = await PostAsync($"/api/v1/orgs/{organizationId}/channels", Alice, new { name = "One too many" });
        create.StatusCode.Should().Be((HttpStatusCode)429, "20 channels were created within the hour");
        (await ErrorCodeAsync(create)).Should().Be("messaging.rate_limited");
        (await QuerySingleAsync("SELECT count(*) FROM messaging.conversations WHERE org_id = $1 AND name = 'One too many'", organizationId)).Should().Equal(0L);
    }

    [Theory]
    [InlineData("deactivated")]
    [InlineData("removed")]
    public async Task CreateAndAdd_WithSomeoneNoLongerActiveInTheOrganization_AreRefusedAsync(string status)
    {
        var organizationId = await ProvisionForAsync(Alice);
        var gone = await AddSilentMemberAsync(organizationId);
        await ExecuteAsync("UPDATE organization.members SET status = $3 WHERE org_id = $1 AND account_id = $2", organizationId, gone, status);
        var channelId = await CreatePrivateChannelAsync(organizationId, Alice, SecretName);

        using var create = await PostAsync(
            $"/api/v1/orgs/{organizationId}/channels", Alice, new { name = "With them", @private = true, memberAccountIds = new[] { gone } });
        using var createPublic = await PostAsync($"/api/v1/orgs/{organizationId}/channels", Alice, new { name = "With them", memberAccountIds = new[] { gone } });
        using var add = await PostAsync($"/api/v1/orgs/{organizationId}/conversations/{channelId}/members", Alice, new { accountIds = new[] { gone } });

        foreach (var response in new[] { create, createPublic, add })
        {
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await ErrorCodeAsync(response)).Should().Be("messaging.member_not_in_organization");
        }

        (await QuerySingleAsync("SELECT count(*) FROM messaging.conversation_members WHERE org_id = $1 AND account_id = $2", organizationId, gone)).Should().Equal(0L);
        (await QuerySingleAsync("SELECT count(*) FROM messaging.conversations WHERE org_id = $1", organizationId)).Should().Equal(1L);
    }

    /// <summary>
    /// The owner and the member who would succeed them leave at the same moment. Both requests wait for the
    /// conversation row; the one that runs second must see what the first committed (it is the owner now), or the
    /// channel would end up owned by someone who left.
    /// </summary>
    [Fact]
    public async Task Leave_ByTheOwnerAndTheSuccessorAtOnce_HandsTheChannelToARemainingMemberAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddMemberAsync(organizationId, Bob, SystemRoles.Member);
        var bob = await AccountAsync(Bob);
        var remaining = await AddSilentMemberAsync(organizationId);
        // Bob joined first, so he is the successor; the silent member joined later.
        var channelId = await CreatePrivateChannelAsync(organizationId, Alice, SecretName, bob);
        await ExecuteAsync("UPDATE messaging.conversation_members SET joined_at = joined_at - interval '1 hour' WHERE conversation_id = $1 AND account_id = $2", channelId, bob);
        (await PostAsync($"/api/v1/orgs/{organizationId}/conversations/{channelId}/members", Alice, new { accountIds = new[] { remaining } })).EnsureSuccessStatusCode();
        var leave = $"/api/v1/orgs/{organizationId}/conversations/{channelId}/leave";

        var statuses = await RaceAsync(channelId, () => SendAsync(HttpMethod.Post, leave, Alice, null), () => SendAsync(HttpMethod.Post, leave, Bob, null));

        statuses.Should().AllBeEquivalentTo(HttpStatusCode.NoContent);
        (await QuerySingleAsync("SELECT owner_account_id, member_count FROM messaging.conversations WHERE id = $1", channelId)).Should().Equal(remaining, 1);
        await OwnerShouldBeACurrentMemberAsync(channelId);
    }

    /// <summary>The owner hands the channel to a member who is leaving at the same moment, in either order.</summary>
    [Fact]
    public async Task TransferOwnership_WhileTheNewOwnerLeaves_NeverLeavesTheChannelToSomeoneWhoLeftAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddMemberAsync(organizationId, Bob, SystemRoles.Member);
        var alice = await AccountAsync(Alice);
        var bob = await AccountAsync(Bob);
        var channelId = await CreatePrivateChannelAsync(organizationId, Alice, SecretName, bob);
        var basePath = $"/api/v1/orgs/{organizationId}/conversations/{channelId}";

        var statuses = await RaceAsync(
            channelId,
            () => SendAsync(HttpMethod.Put, $"{basePath}/owner", Alice, new { accountId = bob }),
            () => SendAsync(HttpMethod.Post, $"{basePath}/leave", Bob, null));

        // Transfer first: Bob leaves as the owner and it passes back. Leave first: the transfer is refused.
        statuses[0].Should().BeOneOf(HttpStatusCode.NoContent, HttpStatusCode.Conflict);
        statuses[1].Should().Be(HttpStatusCode.NoContent);
        (await QuerySingleAsync("SELECT owner_account_id, member_count FROM messaging.conversations WHERE id = $1", channelId)).Should().Equal(alice, 1);
        await OwnerShouldBeACurrentMemberAsync(channelId);
    }

    private async Task OwnerShouldBeACurrentMemberAsync(Guid channelId) =>
        (await QuerySingleAsync(
            """
            SELECT
                (SELECT count(*) FROM messaging.conversation_members m WHERE m.conversation_id = c.id AND m.role = 'owner' AND m.left_at IS NULL),
                EXISTS (
                    SELECT 1 FROM messaging.conversation_members m
                    WHERE m.conversation_id = c.id AND m.account_id = c.owner_account_id AND m.role = 'owner' AND m.left_at IS NULL),
                c.member_count = (SELECT count(*) FROM messaging.conversation_members m WHERE m.conversation_id = c.id AND m.left_at IS NULL)
            FROM messaging.conversations c WHERE c.id = $1
            """,
            channelId)).Should().Equal(1L, true, true);

    /// <summary>
    /// Runs the requests so that all of them wait for the conversation's row lock at the same time: the test holds the
    /// lock, starts them, waits until each is queued behind it and lets go. Each request's locking statement has then
    /// started before the others committed, which is the case a stale read would get wrong.
    /// </summary>
    private async Task<HttpStatusCode[]> RaceAsync(Guid conversationId, params Func<Task<HttpResponseMessage>>[] requests)
    {
        Task<HttpResponseMessage>[] running;
        await using (var holder = new NpgsqlConnection(Database.SuperuserConnectionString))
        {
            await holder.OpenAsync(CancellationToken);
            await using var transaction = await holder.BeginTransactionAsync(CancellationToken);
            await using (var take = new NpgsqlCommand("SELECT 1 FROM messaging.conversations WHERE id = $1 FOR UPDATE", holder, transaction))
            {
                take.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = conversationId });
                await take.ExecuteScalarAsync(CancellationToken);
            }

            running = [.. requests.Select(request => Task.Run(request, CancellationToken))];
            var waited = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                // The first waiter waits for the holder's transaction, the others for the row itself.
                var queued = (long)(await QuerySingleAsync("SELECT count(*) FROM pg_locks WHERE NOT granted AND locktype IN ('transactionid', 'tuple')"))[0]!;
                if (queued >= requests.Length)
                {
                    break;
                }

                waited.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30), "every request should reach the conversation row lock");
                await Task.Delay(TimeSpan.FromMilliseconds(50), CancellationToken);
            }

            await transaction.CommitAsync(CancellationToken);
        }

        var responses = await Task.WhenAll(running);
        var statuses = responses.Select(response => response.StatusCode).ToArray();
        foreach (var response in responses)
        {
            response.Dispose();
        }

        return statuses;
    }
}
