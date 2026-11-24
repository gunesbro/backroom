using System.Net;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Backroom.Modules.Messaging.Contracts;
using Backroom.Modules.Permissions.Contracts;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;

namespace Backroom.Modules.Messaging.IntegrationTests;

/// <summary>
/// Conversations and membership (docs/contracts/phase-2.md, flow 1, step 1): the creator's membership, joining and
/// leaving public channels, read access through <see cref="IConversationReadAccess"/>, and public-only name
/// uniqueness, with real tokens and real row-level security. Private channels and DMs are inserted directly, because
/// their routes arrive in later steps.
/// </summary>
[Collection(CellCollection.Name)]
[Trait("Category", "Privacy")]
public sealed class ConversationTests(PostgresDatabase database, KeycloakRealm keycloak) : MessagingTestBase(database, keycloak)
{
    [Fact]
    public async Task CreateChannel_MakesTheCreatorAMemberAndWritesIdOnlyEventsAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var alice = await AccountAsync(Alice);

        var channelId = await CreateChannelAsync(organizationId, Alice, "General");

        (await QuerySingleAsync("SELECT kind, member_count FROM messaging.conversations WHERE id = $1", channelId))
            .Should().Equal("public_channel", 1);
        (await QuerySingleAsync(
            "SELECT role, joined_seq, added_by, left_at IS NULL FROM messaging.conversation_members WHERE conversation_id = $1 AND account_id = $2",
            channelId, alice)).Should().Equal("member", 0L, alice, true);
        var events = await OutboxAsync(organizationId);
        var created = events.Should().ContainSingle(entry => entry.Type == ConversationCreated.EventType).Subject.Payload.AsObject();
        created.Select(property => property.Key).Should().BeEquivalentTo(["event_id", "org_id", "occurred_at", "conversation_id", "kind", "created_by"]);
        created["kind"]!.GetValue<string>().Should().Be("public_channel");
        var added = events.Should().ContainSingle(entry => entry.Type == ConversationMemberAdded.EventType).Subject.Payload.AsObject();
        added.Select(property => property.Key).Should().BeEquivalentTo(["event_id", "org_id", "occurred_at", "conversation_id", "account_id", "actor_account_id"]);
        added["account_id"]!.GetValue<Guid>().Should().Be(alice);
        events.Select(entry => entry.Payload.ToJsonString()).Should().NotContain(payload => payload.Contains("General", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Join_PublicChannel_OpensTheMembershipAtTheLastSeqAsync()
    {
        var (organizationId, channelId) = await ChannelWithBobAsync();
        await SendMessageAsync(organizationId, channelId, Alice, "one");
        await SendMessageAsync(organizationId, channelId, Alice, "two");
        var bob = await AccountAsync(Bob);

        using var response = await JoinAsync(organizationId, channelId, Bob);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await QuerySingleAsync(
            "SELECT role, joined_seq, added_by, left_at IS NULL, org_id FROM messaging.conversation_members WHERE conversation_id = $1 AND account_id = $2",
            channelId, bob)).Should().Equal("member", 2L, bob, true, organizationId);
        (await MemberCountAsync(channelId)).Should().Be(2);
        var added = (await OutboxAsync(organizationId)).Where(entry => entry.Type == ConversationMemberAdded.EventType)
            .Select(entry => entry.Payload).Should().ContainSingle(payload => payload["account_id"]!.GetValue<Guid>() == bob).Subject;
        added["conversation_id"]!.GetValue<Guid>().Should().Be(channelId);
        added["actor_account_id"]!.GetValue<Guid>().Should().Be(bob);
    }

    [Fact]
    public async Task Join_WhenAlreadyAMember_ChangesNothingAsync()
    {
        var (organizationId, channelId) = await ChannelWithBobAsync();
        (await JoinAsync(organizationId, channelId, Bob)).Dispose();

        using var again = await JoinAsync(organizationId, channelId, Bob);
        using var creator = await JoinAsync(organizationId, channelId, Alice);

        again.StatusCode.Should().Be(HttpStatusCode.NoContent);
        creator.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await MemberCountAsync(channelId)).Should().Be(2);
        (await OutboxAsync(organizationId)).Count(entry => entry.Type == ConversationMemberAdded.EventType).Should().Be(2, "the creator's and Bob's first join");
    }

    [Fact]
    public async Task Leave_ThenJoin_ClosesAndReopensTheSameMembershipAsync()
    {
        var (organizationId, channelId) = await ChannelWithBobAsync();
        var bob = await AccountAsync(Bob);
        (await JoinAsync(organizationId, channelId, Bob)).Dispose();

        using var left = await LeaveAsync(organizationId, channelId, Bob);

        left.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await QuerySingleAsync("SELECT left_at IS NOT NULL FROM messaging.conversation_members WHERE conversation_id = $1 AND account_id = $2", channelId, bob))
            .Should().Equal(true);
        (await MemberCountAsync(channelId)).Should().Be(1);
        var removed = (await OutboxAsync(organizationId)).Should().ContainSingle(entry => entry.Type == ConversationMemberRemoved.EventType).Subject.Payload.AsObject();
        removed.Select(property => property.Key).Should().BeEquivalentTo(["event_id", "org_id", "occurred_at", "conversation_id", "account_id", "actor_account_id"]);
        removed["account_id"]!.GetValue<Guid>().Should().Be(bob);
        Publisher.Removals.Should().Equal([(organizationId, channelId, bob)], "the live group is left after the commit");

        await SendMessageAsync(organizationId, channelId, Alice, "while Bob was away");
        using var rejoined = await JoinAsync(organizationId, channelId, Bob);

        rejoined.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await QuerySingleAsync(
            "SELECT count(*), bool_and(left_at IS NULL), max(joined_seq) FROM messaging.conversation_members WHERE conversation_id = $1 AND account_id = $2",
            channelId, bob)).Should().Equal(1L, true, 1L);
        (await MemberCountAsync(channelId)).Should().Be(2);
    }

    [Fact]
    public async Task Leave_WhenNotAMember_ChangesNothingAsync()
    {
        var (organizationId, channelId) = await ChannelWithBobAsync();

        using var response = await LeaveAsync(organizationId, channelId, Bob);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await MemberCountAsync(channelId)).Should().Be(1);
        (await OutboxAsync(organizationId)).Should().NotContain(entry => entry.Type == ConversationMemberRemoved.EventType);
        Publisher.Removals.Should().BeEmpty();
    }

    [Fact]
    public async Task Leave_TheLastMember_LeavesTheChannelReadableAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(organizationId, Alice, "General");

        (await LeaveAsync(organizationId, channelId, Alice)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await MemberCountAsync(channelId)).Should().Be(0);
        (await GetAsync($"/api/v1/orgs/{organizationId}/channels/{channelId}/messages", Alice)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Join_ArchivedPublicChannel_IsConflictAsync()
    {
        var (organizationId, channelId) = await ChannelWithBobAsync();
        await ExecuteAsync("UPDATE messaging.conversations SET archived_at = now() WHERE id = $1", channelId);

        using var response = await JoinAsync(organizationId, channelId, Bob);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadJsonAsync(response))["code"]!.GetValue<string>().Should().Be("messaging.conversation_archived");
    }

    [Fact]
    public async Task Leave_Dm_IsConflictAsync()
    {
        var (organizationId, _) = await ChannelWithBobAsync();
        var dmId = await InsertConversationAsync(organizationId, ConversationKinds.Dm, null, Alice, Bob);

        using var response = await LeaveAsync(organizationId, dmId, Bob);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadJsonAsync(response))["code"]!.GetValue<string>().Should().Be("messaging.cannot_leave_dm");
    }

    [Fact]
    public async Task JoinAndLeave_UnknownForeignOrUnreadable_AreTheSameNotFoundAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddMemberAsync(organizationId, Bob, SystemRoles.Member);
        var otherOrganization = await ProvisionForAsync(Bob);
        var foreignChannel = await CreateChannelAsync(otherOrganization, Bob, "Elsewhere");
        var bobsPrivate = await InsertConversationAsync(organizationId, ConversationKinds.PrivateChannel, "Secret", Bob);
        var bobsDm = await InsertConversationAsync(organizationId, ConversationKinds.Dm, null, Bob);

        var bodies = new List<string>();
        foreach (var id in new[] { Guid.NewGuid(), foreignChannel, bobsPrivate, bobsDm })
        {
            foreach (var action in new Func<Guid, Guid, string, Task<HttpResponseMessage>>[] { JoinAsync, LeaveAsync })
            {
                // Alice is the organization's Owner: no role gives access to a conversation she is not in.
                using var response = await action(organizationId, id, Alice);
                response.StatusCode.Should().Be(HttpStatusCode.NotFound);
                var body = (await ReadJsonAsync(response)).AsObject();
                body["code"]!.GetValue<string>().Should().Be("messaging.conversation_not_found");
                body.Remove("traceId");
                bodies.Add(body.ToJsonString());
            }
        }

        bodies.Distinct(StringComparer.Ordinal).Should().ContainSingle("an unreadable conversation looks like an unknown one");
        (await QuerySingleAsync("SELECT count(*) FROM messaging.conversation_members WHERE org_id = $1 AND account_id = $2", organizationId, await AccountAsync(Alice)))
            .Should().Equal(0L);
    }

    [Fact]
    public async Task PrivateConversation_ForTheOwnerRoleWithoutMembership_IsNotFoundOnEveryRouteAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddMemberAsync(organizationId, Bob, SystemRoles.Member);
        var privateId = await InsertConversationAsync(organizationId, ConversationKinds.PrivateChannel, "Secret", Bob);

        using var list = await GetAsync($"/api/v1/orgs/{organizationId}/channels/{privateId}/messages", Alice);
        using var send = await PostAsync($"/api/v1/orgs/{organizationId}/channels/{privateId}/messages", Alice, new { clientMessageId = Guid.NewGuid(), text = "hi" });
        using var channels = await GetAsync($"/api/v1/orgs/{organizationId}/channels", Alice);

        list.StatusCode.Should().Be(HttpStatusCode.NotFound);
        send.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadJsonAsync(send))["code"]!.GetValue<string>().Should().Be("messaging.channel_not_found");
        (await ReadJsonAsync(channels))["channels"]!.AsArray().Should().BeEmpty("the public list never shows a private channel");
        (await QuerySingleAsync("SELECT count(*) FROM messaging.messages WHERE conversation_id = $1", privateId))[0].Should().Be(0L);
    }

    [Fact]
    public async Task PrivateConversation_ForAMember_IsReadableAndWritableThroughPhase1RoutesAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddMemberAsync(organizationId, Bob, SystemRoles.Member);
        var privateId = await InsertConversationAsync(organizationId, ConversationKinds.PrivateChannel, "Secret", Bob);

        await SendMessageAsync(organizationId, privateId, Bob, "members only");
        using var list = await GetAsync($"/api/v1/orgs/{organizationId}/channels/{privateId}/messages", Bob);
        using var join = await JoinAsync(organizationId, privateId, Bob);

        list.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(list))["messages"]!.AsArray().Should().ContainSingle();
        join.StatusCode.Should().Be(HttpStatusCode.NoContent, "a member joining changes nothing");
    }

    [Fact]
    public async Task ReadAccess_FollowsKindArchiveAndMembershipAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddMemberAsync(organizationId, Bob, SystemRoles.Member);
        var otherOrganization = await ProvisionForAsync(Bob);
        var alice = await AccountAsync(Alice);
        var bob = await AccountAsync(Bob);
        var publicId = await CreateChannelAsync(organizationId, Alice, "General");
        var archived = await CreateChannelAsync(organizationId, Alice, "Old");
        await ExecuteAsync("UPDATE messaging.conversations SET archived_at = now() WHERE id = $1", archived);
        var privateId = await InsertConversationAsync(organizationId, ConversationKinds.PrivateChannel, "Secret", Bob);
        var groupDm = await InsertConversationAsync(organizationId, ConversationKinds.GroupDm, null, Bob);
        var foreign = await CreateChannelAsync(otherOrganization, Bob, "Elsewhere");

        (await CanReadAsync(organizationId, publicId, bob)).Should().BeTrue("every member reads a public channel without joining");
        (await CanReadAsync(organizationId, archived, alice)).Should().BeFalse("archived conversations wait for the archive step");
        (await CanReadAsync(organizationId, privateId, bob)).Should().BeTrue();
        (await CanReadAsync(organizationId, privateId, alice)).Should().BeFalse("the Owner role without membership");
        (await CanReadAsync(organizationId, groupDm, bob)).Should().BeTrue();
        (await CanReadAsync(organizationId, groupDm, alice)).Should().BeFalse();
        (await CanReadAsync(organizationId, foreign, bob)).Should().BeFalse("row-level security hides other organizations");
        (await CanReadAsync(organizationId, Guid.NewGuid(), alice)).Should().BeFalse();

        await ExecuteAsync("UPDATE messaging.conversation_members SET left_at = now() WHERE conversation_id = $1", privateId);
        (await CanReadAsync(organizationId, privateId, bob)).Should().BeFalse("leaving ends read access at once");
    }

    [Fact]
    public async Task CreateChannel_WhenNameMatchesPrivateChannelOfOthers_SucceedsAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddMemberAsync(organizationId, Bob, SystemRoles.Member);
        await InsertConversationAsync(organizationId, ConversationKinds.PrivateChannel, "Secret plans", Bob);
        using var response = await PostAsync($"/api/v1/orgs/{organizationId}/channels", Alice, new { name = "SECRET PLANS" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = (await ReadJsonAsync(response)).AsObject();
        body.Select(property => property.Key).Should().BeEquivalentTo(["id", "name", "createdAt"], "the answer is the same as for any new name");
        body["name"]!.GetValue<string>().Should().Be("SECRET PLANS");
        Publisher.Channels.Should().ContainSingle().Which.Channel.Name.Should().Be("SECRET PLANS");
        (await QuerySingleAsync("SELECT count(*) FROM messaging.conversations WHERE org_id = $1 AND name_key = 'secret plans'", organizationId))[0]
            .Should().Be(2L, "the private channel is untouched and the public one sits beside it");
    }

    [Fact]
    public async Task CreateChannel_WhenNameMatchesOpenPublicChannel_IsConflictCaseInsensitivelyAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await CreateChannelAsync(organizationId, Alice, "General");

        using var response = await PostAsync($"/api/v1/orgs/{organizationId}/channels", Alice, new { name = "GENERAL" });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadJsonAsync(response))["code"]!.GetValue<string>().Should().Be("messaging.channel_name_taken");
    }

    [Fact]
    public async Task PrivateChannelNames_AreNotUniqueAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddMemberAsync(organizationId, Bob, SystemRoles.Member);
        await CreateChannelAsync(organizationId, Alice, "Design");

        await InsertConversationAsync(organizationId, ConversationKinds.PrivateChannel, "Design", Bob);
        await InsertConversationAsync(organizationId, ConversationKinds.PrivateChannel, "Design", Alice);

        (await QuerySingleAsync("SELECT count(*) FROM messaging.conversations WHERE org_id = $1 AND name_key = 'design'", organizationId))
            .Should().Equal(3L);
    }

    [Fact]
    public async Task Join_AtTheMembershipLimit_IsConflictAndCreateStoresNothingAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddMemberAsync(organizationId, Bob, SystemRoles.Member);
        var channelId = await CreateChannelAsync(organizationId, Alice, "General");
        var bob = await AccountAsync(Bob);
        await ExecuteAsync(
            """
            WITH created AS (
                INSERT INTO messaging.conversations (id, org_id, kind, name, name_key, created_by, created_at, member_count)
                SELECT gen_random_uuid(), $1, 'private_channel', 'p', 'p', $2, now(), 1 FROM generate_series(1, 10000)
                RETURNING id)
            INSERT INTO messaging.conversation_members (org_id, conversation_id, account_id, role, joined_at, joined_seq, added_by)
            SELECT $1, id, $2, 'owner', now(), 0, $2 FROM created
            """,
            organizationId, bob);
        using var join = await JoinAsync(organizationId, channelId, Bob);
        using var create = await PostAsync($"/api/v1/orgs/{organizationId}/channels", Bob, new { name = "One too many" });

        join.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadJsonAsync(join))["code"]!.GetValue<string>().Should().Be("messaging.membership_limit");
        create.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await QuerySingleAsync("SELECT count(*) FROM messaging.conversations WHERE org_id = $1 AND kind = 'public_channel'", organizationId))[0]
            .Should().Be(1L, "the refused channel is rolled back with its membership");
    }

    [Fact]
    public async Task ConversationMembers_UnderRowLevelSecurity_AreInvisibleAndUnwritableForOtherOrganizationsAsync()
    {
        var (organizationId, channelId) = await ChannelWithBobAsync();
        var otherOrganization = await ProvisionForAsync(Bob);

        await using var connection = new Npgsql.NpgsqlConnection(Database.ApplicationConnectionString());
        await connection.OpenAsync(CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(CancellationToken);
        await using (var enter = new Npgsql.NpgsqlCommand("SELECT set_config('app.org_id', $1, true)", connection, transaction))
        {
            enter.Parameters.Add(new Npgsql.NpgsqlParameter<string> { TypedValue = otherOrganization.ToString("D") });
            await enter.ExecuteNonQueryAsync(CancellationToken);
        }

        await using (var count = new Npgsql.NpgsqlCommand("SELECT count(*) FROM messaging.conversation_members WHERE conversation_id = $1", connection, transaction))
        {
            count.Parameters.Add(new Npgsql.NpgsqlParameter<Guid> { TypedValue = channelId });
            (await count.ExecuteScalarAsync(CancellationToken)).Should().Be(0L);
        }

        await using var insert = new Npgsql.NpgsqlCommand(
            """
            INSERT INTO messaging.conversation_members (org_id, conversation_id, account_id, role, joined_at, joined_seq, added_by)
            VALUES ($1, $2, $3, 'member', now(), 0, $3)
            """,
            connection,
            transaction);
        insert.Parameters.Add(new Npgsql.NpgsqlParameter<Guid> { TypedValue = organizationId });
        insert.Parameters.Add(new Npgsql.NpgsqlParameter<Guid> { TypedValue = channelId });
        insert.Parameters.Add(new Npgsql.NpgsqlParameter<Guid> { TypedValue = await AccountAsync(Bob) });
        var failure = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => insert.ExecuteNonQueryAsync(CancellationToken));
        failure.SqlState.Should().Be("42501", "the policy's WITH CHECK refuses a row of another organization");
    }

    private async Task<(Guid OrganizationId, Guid ChannelId)> ChannelWithBobAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddMemberAsync(organizationId, Bob, null);
        return (organizationId, await CreateChannelAsync(organizationId, Alice, "General"));
    }

    private Task<HttpResponseMessage> JoinAsync(Guid organizationId, Guid conversationId, string username) =>
        SendAsync(HttpMethod.Post, $"/api/v1/orgs/{organizationId}/conversations/{conversationId}/join", username, null);

    private Task<HttpResponseMessage> LeaveAsync(Guid organizationId, Guid conversationId, string username) =>
        SendAsync(HttpMethod.Post, $"/api/v1/orgs/{organizationId}/conversations/{conversationId}/leave", username, null);

    private async Task<int> MemberCountAsync(Guid conversationId) =>
        (int)(await QuerySingleAsync("SELECT member_count FROM messaging.conversations WHERE id = $1", conversationId))[0]!;

    /// <summary>A conversation the API cannot create yet, with open memberships for <paramref name="members"/>.</summary>
    private async Task<Guid> InsertConversationAsync(Guid organizationId, string kind, string? name, params string[] members)
    {
        var id = Guid.CreateVersion7();
        var creator = await AccountAsync(members[0]);
        await ExecuteAsync(
            """
            INSERT INTO messaging.conversations (id, org_id, kind, name, name_key, created_by, created_at, member_count)
            VALUES ($1, $2, $3, $4::text, lower($4::text), $5, now(), $6)
            """,
            id, organizationId, kind, (object?)name ?? DBNull.Value, creator, members.Length);
        foreach (var member in members)
        {
            var account = await AccountAsync(member);
            await ExecuteAsync(
                """
                INSERT INTO messaging.conversation_members (org_id, conversation_id, account_id, role, joined_at, joined_seq, added_by)
                VALUES ($1, $2, $3, $4, now(), 0, $5)
                """,
                organizationId, id, account, account == creator && kind == ConversationKinds.PrivateChannel ? "owner" : "member", creator);
        }

        return id;
    }

    private async Task<bool> CanReadAsync(Guid organizationId, Guid conversationId, Guid accountId)
    {
        var canRead = false;
        await InOrganizationAsync(organizationId, async services =>
            canRead = await services.GetRequiredService<IConversationReadAccess>().CanReadAsync(organizationId, conversationId, accountId, CancellationToken));
        return canRead;
    }
}
