using System.Net;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;
using Backroom.Modules.Permissions.Contracts;

namespace Backroom.Modules.Messaging.IntegrationTests;

/// <summary>
/// Sending and reading plain-text messages (docs/contracts/phase-1.md, flow 4, steps 2 to 5) with real tokens and
/// real row-level security.
/// </summary>
[Collection(CellCollection.Name)]
[Trait("Category", "Privacy")]
public sealed class MessageTests(PostgresDatabase database, KeycloakRealm keycloak) : MessagingTestBase(database, keycloak)
{
    [Fact]
    public async Task Send_AsAMember_StoresTheRawTextWithBlocksDerivedOnTheServerAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        var clientMessageId = Guid.NewGuid();

        using var response = await PostAsync(
            MessagesPath(organizationId, channelId),
            Alice,
            new { clientMessageId, text = "  <b>Hi</b> & \"bye\" 'x'\r\nnext  " });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = (await ReadJsonAsync(response)).AsObject();
        body.Select(property => property.Key).Should().BeEquivalentTo(["id", "seq", "versionSeq", "clientMessageId", "authorAccountId", "text", "blocks", "createdAt", "deletedAt", "editedAt", "epoch"]);
        const string Stored = "<b>Hi</b> & \"bye\" 'x'\nnext";
        body["text"]!.GetValue<string>().Should().Be(Stored);
        body["clientMessageId"]!.GetValue<Guid>().Should().Be(clientMessageId);
        body["authorAccountId"]!.GetValue<Guid>().Should().Be(await AccountAsync(Alice));
        var id = body["id"]!.GetValue<Guid>();
        id.Version.Should().Be(7);

        var row = await QuerySingleAsync(
            "SELECT org_id, conversation_id, text, jsonb_array_length(blocks), blocks->0->>'type', blocks->0->'children'->0->>'type', blocks->0->'children'->0->>'text', jsonb_typeof(blocks) FROM messaging.messages WHERE id = $1", id);
        row.Should().Equal(organizationId, channelId, Stored, 1, "paragraph", "text", Stored, "array");
        var blocks = body["blocks"]!.AsArray();
        blocks.Should().ContainSingle();
        blocks[0]!["type"]!.GetValue<string>().Should().Be("paragraph");
        blocks[0]!["children"]!.AsArray().Should().ContainSingle().Which!["text"]!.GetValue<string>().Should().Be(Stored);
    }

    [Fact]
    public async Task Send_WithBlocksWhileRichTextIsOff_IsRefusedAndStoresNothingAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        var blocks = new[] { new { type = "paragraph", children = new[] { new { type = "text", text = "hi" } } } };

        using var response = await PostAsync(MessagesPath(organizationId, channelId), Alice, new { clientMessageId = Guid.NewGuid(), text = "hi", blocks });

        await ShouldBeInvalidAsync(response);
        (await ReadJsonAsync(response))["reason"]!.GetValue<string>().Should().Be("blocks.disabled", "formatting is refused, never dropped silently");
        await ShouldHaveStoredNothingAsync(organizationId, channelId);
    }

    [Fact]
    public async Task Send_ARepeatOfTheClientMessageId_ReturnsTheExistingMessageAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        var clientMessageId = Guid.NewGuid();
        using var first = await PostAsync(MessagesPath(organizationId, channelId), Alice, new { clientMessageId, text = "first" });

        using var repeat = await PostAsync(MessagesPath(organizationId, channelId), Alice, new { clientMessageId, text = "something else" });

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        repeat.StatusCode.Should().Be(HttpStatusCode.OK);
        var firstBody = await ReadJsonAsync(first);
        var repeatBody = await ReadJsonAsync(repeat);
        repeatBody.ToJsonString().Should().Be(firstBody.ToJsonString(), "a repeat gets the stored message, not a new one");
        (await QuerySingleAsync("SELECT count(*) FROM messaging.messages WHERE conversation_id = $1", channelId))[0].Should().Be(1L);
        (await PostedEventsAsync(organizationId)).Should().ContainSingle("the repeat publishes nothing");
        Publisher.Messages.Should().ContainSingle("the repeat announces nothing");
    }

    [Fact]
    public async Task Send_TheSameClientMessageIdAtOnce_StoresOneMessageAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        var clientMessageId = Guid.NewGuid();

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            using var response = await PostAsync(MessagesPath(organizationId, channelId), Alice, new { clientMessageId, text = "once" });
            return (response.StatusCode, Id: (await ReadJsonAsync(response))["id"]!.GetValue<Guid>());
        }));

        responses.Count(response => response.StatusCode == HttpStatusCode.Created).Should().Be(1);
        responses.Count(response => response.StatusCode == HttpStatusCode.OK).Should().Be(5);
        responses.Select(response => response.Id).Distinct().Should().ContainSingle();
        (await QuerySingleAsync("SELECT count(*) FROM messaging.messages WHERE conversation_id = $1", channelId))[0].Should().Be(1L);
        (await PostedEventsAsync(organizationId)).Should().ContainSingle();
    }

    [Fact]
    public async Task Send_TheSameClientMessageIdFromAnotherAuthor_IsAnotherMessageAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        await AddMemberAsync(organizationId, Bob, SystemRoles.Member);
        (await PostAsync($"/api/v1/orgs/{organizationId}/conversations/{channelId}/join", Bob, new { })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var clientMessageId = Guid.NewGuid();

        using var fromAlice = await PostAsync(MessagesPath(organizationId, channelId), Alice, new { clientMessageId, text = "a" });
        using var fromBob = await PostAsync(MessagesPath(organizationId, channelId), Bob, new { clientMessageId, text = "b" });

        fromAlice.StatusCode.Should().Be(HttpStatusCode.Created);
        fromBob.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Send_WritesTheOutboxEventInTheSameTransactionWithoutTheTextAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();

        var message = await SendMessageAsync(organizationId, channelId, Alice, "text that must stay out of events");

        var events = await PostedEventsAsync(organizationId);
        var published = events.Should().ContainSingle().Subject;
        published.Type.Should().Be("messaging.message-posted.v1");
        var payload = published.Payload.AsObject();
        payload.Select(property => property.Key).Should().BeEquivalentTo(
            ["event_id", "org_id", "occurred_at", "channel_id", "message_id", "author_account_id", "created_at"]);
        payload["org_id"]!.GetValue<Guid>().Should().Be(organizationId);
        payload["channel_id"]!.GetValue<Guid>().Should().Be(channelId);
        payload["message_id"]!.GetValue<Guid>().Should().Be(message["id"]!.GetValue<Guid>());
        payload["author_account_id"]!.GetValue<Guid>().Should().Be(await AccountAsync(Alice));
        payload.ToJsonString().Should().NotContain("text that must stay out");
    }

    [Fact]
    public async Task Send_AfterTheCommit_PublishesMessagePostedWithTheTextAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        long? storedWhenPublished = null;
        Publisher.OnPublish = async () =>
            storedWhenPublished = (long)(await QuerySingleAsync("SELECT count(*) FROM messaging.messages WHERE conversation_id = $1", channelId))[0]!;
        var clientMessageId = Guid.NewGuid();

        var message = await SendMessageAsync(organizationId, channelId, Alice, "hello <all>", clientMessageId);

        storedWhenPublished.Should().Be(1, "live events are sent after the commit, never inside the transaction");
        var published = Publisher.Messages.Should().ContainSingle().Subject;
        published.OrgId.Should().Be(organizationId);
        published.ChannelId.Should().Be(channelId);
        published.Message.Id.Should().Be(message["id"]!.GetValue<Guid>());
        published.Message.ClientMessageId.Should().Be(clientMessageId);
        published.Message.AuthorAccountId.Should().Be(await AccountAsync(Alice));
        published.Message.Text.Should().Be("hello <all>");
        published.Message.CreatedAt.Should().Be(message["createdAt"]!.GetValue<DateTimeOffset>());
    }

    [Fact]
    public async Task Send_WhenPublishingFails_StillSucceedsAndLogsOnlyIdsAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        Publisher.Fail = true;

        using var response = await PostAsync(
            MessagesPath(organizationId, channelId), Alice, new { clientMessageId = Guid.NewGuid(), text = "FAILING-PUBLISH-TEXT" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        (await QuerySingleAsync("SELECT count(*) FROM messaging.messages WHERE conversation_id = $1", channelId))[0].Should().Be(1L);
        (await PostedEventsAsync(organizationId)).Should().ContainSingle();
        Logs.Entries.Should().Contain(entry => entry.Contains("Publishing the live event MessagePosted failed", StringComparison.Ordinal));
        Logs.Entries.Should().NotContain(entry => entry.Contains("FAILING-PUBLISH-TEXT", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\t  ")]
    [InlineData("bell\a")]
    [InlineData("nul\0here")]
    public async Task Send_WithEmptyOrControlCharacterText_IsRefusedAsync(string text)
    {
        var (organizationId, channelId) = await ChannelAsync();

        using var response = await PostAsync(MessagesPath(organizationId, channelId), Alice, new { clientMessageId = Guid.NewGuid(), text });

        await ShouldBeInvalidAsync(response);
        await ShouldHaveStoredNothingAsync(organizationId, channelId);
    }

    [Fact]
    public async Task Send_WithoutTextOrWithoutAClientMessageId_IsRefusedAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();

        using var noText = await PostAsync(MessagesPath(organizationId, channelId), Alice, new { clientMessageId = Guid.NewGuid() });
        using var noId = await PostAsync(MessagesPath(organizationId, channelId), Alice, new { text = "hi" });
        using var emptyId = await PostAsync(MessagesPath(organizationId, channelId), Alice, new { clientMessageId = Guid.Empty, text = "hi" });

        await ShouldBeInvalidAsync(noText);
        await ShouldBeInvalidAsync(noId);
        await ShouldBeInvalidAsync(emptyId);
        await ShouldHaveStoredNothingAsync(organizationId, channelId);
    }

    [Fact]
    public async Task Send_WithTheLongestText_SucceedsAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();

        // Nothing is escaped or expanded: the text is stored as sent.
        var longest = await SendMessageAsync(organizationId, channelId, Alice, new string('"', 4000));

        longest["text"]!.GetValue<string>().Should().HaveLength(4000);
    }

    [Fact]
    public async Task Send_WithOneCharacterTooMany_IsRefusedAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();

        using var response = await PostAsync(MessagesPath(organizationId, channelId), Alice, new { clientMessageId = Guid.NewGuid(), text = new string('a', 4001) });

        await ShouldBeInvalidAsync(response);
        await ShouldHaveStoredNothingAsync(organizationId, channelId);
    }

    [Fact]
    public async Task Send_ToAnUnknownChannelIsNotFound_AndToAnArchivedOneIsRefusedAsReadOnlyAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        var archived = await CreateChannelAsync(organizationId, Alice, "Old");
        await ExecuteAsync("UPDATE messaging.conversations SET archived_at = now() WHERE id = $1", archived);

        using var unknown = await PostAsync(MessagesPath(organizationId, Guid.CreateVersion7()), Alice, new { clientMessageId = Guid.NewGuid(), text = "hi" });
        using var closed = await PostAsync(MessagesPath(organizationId, archived), Alice, new { clientMessageId = Guid.NewGuid(), text = "hi" });

        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadJsonAsync(unknown))["code"]!.GetValue<string>().Should().Be("messaging.conversation_not_found");
        closed.StatusCode.Should().Be(HttpStatusCode.Conflict, "an archived conversation is read-only");
        (await ReadJsonAsync(closed))["code"]!.GetValue<string>().Should().Be("messaging.conversation_archived");
        (await QuerySingleAsync("SELECT count(*) FROM messaging.messages WHERE org_id = $1", organizationId))[0].Should().Be(0L);
        using var history = await GetAsync(MessagesPath(organizationId, archived), Alice);
        history.StatusCode.Should().Be(HttpStatusCode.OK, "and stays readable");
        channelId.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Send_ToAChannelOfAnotherOrganization_IsNotFoundAsync()
    {
        var (mine, _) = await ChannelAsync();
        var (_, foreignChannel) = await ChannelAsync();

        using var response = await PostAsync(MessagesPath(mine, foreignChannel), Alice, new { clientMessageId = Guid.NewGuid(), text = "hi" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadJsonAsync(response))["code"]!.GetValue<string>().Should().Be("messaging.conversation_not_found");
        (await QuerySingleAsync("SELECT count(*) FROM messaging.messages WHERE conversation_id = $1", foreignChannel))[0].Should().Be(0L);
    }

    [Fact]
    public async Task Send_WithoutJoiningAPublicChannel_IsForbiddenAndStoresNothingAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        await AddMemberAsync(organizationId, Bob, SystemRoles.Member);

        using var response = await PostAsync(MessagesPath(organizationId, channelId), Bob, new { clientMessageId = Guid.NewGuid(), text = "hi" });
        using var read = await GetAsync(MessagesPath(organizationId, channelId), Bob);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReadJsonAsync(response))["code"]!.GetValue<string>().Should().Be("messaging.not_a_member");
        read.StatusCode.Should().Be(HttpStatusCode.OK, "an open public channel is readable before joining");
        await ShouldHaveStoredNothingAsync(organizationId, channelId);
    }

    [Fact]
    public async Task PhaseOneChannelMessageRoutes_AreRemovedAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        var path = $"/api/v1/orgs/{organizationId}/channels/{channelId}/messages";

        using var list = await GetAsync(path, Alice);
        using var send = await PostAsync(path, Alice, new { clientMessageId = Guid.NewGuid(), text = "hi" });

        list.StatusCode.Should().Be(HttpStatusCode.NotFound);
        send.StatusCode.Should().Be(HttpStatusCode.NotFound);
        await ShouldHaveStoredNothingAsync(organizationId, channelId);
    }

    [Fact]
    public async Task ListConversations_ListsOnlyTheCallersOpenMembershipsAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        await AddMemberAsync(organizationId, Bob, SystemRoles.Member);
        var other = await CreateChannelAsync(organizationId, Alice, "Other");
        var bobBefore = await ReadJsonAsync(await GetAsync($"/api/v1/orgs/{organizationId}/conversations", Bob));
        (await PostAsync($"/api/v1/orgs/{organizationId}/conversations/{channelId}/join", Bob, new { })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var bobAfter = await ReadJsonAsync(await GetAsync($"/api/v1/orgs/{organizationId}/conversations", Bob));
        var alice = await ReadJsonAsync(await GetAsync($"/api/v1/orgs/{organizationId}/conversations?limit=1", Alice));

        bobBefore["items"]!.AsArray().Should().BeEmpty();
        bobAfter["items"]!.AsArray().Select(item => item!["id"]!.GetValue<Guid>()).Should().Equal(channelId);
        bobAfter["items"]![0]!["kind"]!.GetValue<string>().Should().Be("public_channel");
        alice["items"]!.AsArray().Should().ContainSingle();
        alice["next"]!.GetValue<string>().Should().NotBeNullOrEmpty();
        other.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ListConversations_WithAnInvalidPageSize_IsRejectedAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);

        using var response = await GetAsync($"/api/v1/orgs/{organizationId}/conversations?limit=101", Alice);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ListConversations_OrdersByNewestMessageAndPagesByAnOpaqueCursorAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var quiet = await CreateChannelAsync(organizationId, Alice, "Quiet");
        var older = await CreateChannelAsync(organizationId, Alice, "Older");
        var newer = await CreateChannelAsync(organizationId, Alice, "Newer");
        await SendMessageAsync(organizationId, older, Alice, "one");
        await SendMessageAsync(organizationId, newer, Alice, "two");
        await SendMessageAsync(organizationId, older, Alice, "three");
        var path = $"/api/v1/orgs/{organizationId}/conversations";

        var first = await ReadJsonAsync(await GetAsync($"{path}?limit=2", Alice));
        var second = await ReadJsonAsync(await GetAsync($"{path}?limit=2&after={Uri.EscapeDataString(first["next"]!.GetValue<string>())}", Alice));

        first["items"]!.AsArray().Select(item => item!["id"]!.GetValue<Guid>()).Should().Equal(older, newer);
        second["items"]!.AsArray().Select(item => item!["id"]!.GetValue<Guid>()).Should().Equal(quiet);
        second["next"].Should().BeNull();
    }

    [Theory]
    [InlineData("not-a-cursor")]
    [InlineData("AAAA")]
    [InlineData("!!!")]
    public async Task ListConversations_WithAMalformedCursor_IsRejectedAsync(string cursor)
    {
        var organizationId = await ProvisionForAsync(Alice);

        using var response = await GetAsync($"/api/v1/orgs/{organizationId}/conversations?after={cursor}", Alice);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadJsonAsync(response))["code"]!.GetValue<string>().Should().Be("messaging.cursor_invalid");
    }
    [Fact]
    public async Task Send_AsSomeoneElse_IsNotFoundAndStoresNothingAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();

        using var response = await PostAsync(MessagesPath(organizationId, channelId), Bob, new { clientMessageId = Guid.NewGuid(), text = "hi" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadJsonAsync(response))["code"]!.GetValue<string>().Should().Be("organization.not_found");
        await ShouldHaveStoredNothingAsync(organizationId, channelId);
    }

    [Fact]
    public async Task Send_WithoutAToken_IsUnauthorizedAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();

        using var response = await PostAsync(MessagesPath(organizationId, channelId), null, new { clientMessageId = Guid.NewGuid(), text = "hi" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Send_ManyInARow_KeepsTheSendingOrderByIdAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        var sent = new List<Guid>();
        for (var index = 0; index < 20; index++)
        {
            sent.Add((await SendMessageAsync(organizationId, channelId, Alice, $"m{index}"))["id"]!.GetValue<Guid>());
        }

        var body = await ReadJsonAsync(await GetAsync($"{MessagesPath(organizationId, channelId)}?after=0&limit=100", Alice));

        Ids(body).Should().Equal(sent);
    }

    [Fact]
    public async Task List_WithoutACursor_ReturnsTheNewestFirstAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        var ids = await SendManyAsync(organizationId, channelId, 7);

        var body = await ReadJsonAsync(await GetAsync(MessagesPath(organizationId, channelId), Alice));

        Ids(body).Should().Equal(Enumerable.Reverse(ids));
        body["next"].Should().BeNull();
        body["items"]![0]!.AsObject().Select(property => property.Key)
            .Should().BeEquivalentTo(["id", "seq", "versionSeq", "clientMessageId", "authorAccountId", "text", "blocks", "createdAt", "deletedAt", "editedAt"]);
    }

    [Fact]
    public async Task List_Before_PagesBackThroughHistoryNewestFirstAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        var ids = await SendManyAsync(organizationId, channelId, 7);

        var first = await ReadJsonAsync(await GetAsync($"{MessagesPath(organizationId, channelId)}?limit=3", Alice));
        var second = await ReadJsonAsync(await GetAsync($"{MessagesPath(organizationId, channelId)}?limit=3&before={first["next"]!.GetValue<long>()}", Alice));
        var third = await ReadJsonAsync(await GetAsync($"{MessagesPath(organizationId, channelId)}?limit=3&before={second["next"]!.GetValue<long>()}", Alice));

        Ids(first).Should().Equal(ids[6], ids[5], ids[4]);
        first["next"]!.GetValue<long>().Should().Be(5);
        Ids(second).Should().Equal(ids[3], ids[2], ids[1]);
        Ids(third).Should().Equal(ids[0]);
        third["next"].Should().BeNull();
    }

    [Fact]
    public async Task List_After_CatchesUpOldestFirstAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        var ids = await SendManyAsync(organizationId, channelId, 7);

        var first = await ReadJsonAsync(await GetAsync($"{MessagesPath(organizationId, channelId)}?after=3&limit=2", Alice));
        var rest = await ReadJsonAsync(await GetAsync($"{MessagesPath(organizationId, channelId)}?after=5", Alice));
        var none = await ReadJsonAsync(await GetAsync($"{MessagesPath(organizationId, channelId)}?after=7", Alice));

        Ids(first).Should().Equal(ids[3], ids[4]);
        first["next"]!.GetValue<long>().Should().Be(5);
        Ids(rest).Should().Equal(ids[5], ids[6]);
        rest["next"].Should().BeNull();
        Ids(none).Should().BeEmpty();
        none["next"].Should().BeNull();
    }

    [Fact]
    public async Task List_WithTheLargestPageSize_ReturnsAtMostThatManyAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        await ExecuteAsync(
            """
            INSERT INTO messaging.messages (id, org_id, conversation_id, seq, version_seq, author_account_id, client_message_id, text, blocks, created_at)
            SELECT uuidv7(), $1, $2, n, n, $3, uuidv7(), 'm', '[]'::jsonb, now() FROM generate_series(1, 120) AS n
            """,
            organizationId, channelId, await AccountAsync(Alice));

        var body = await ReadJsonAsync(await GetAsync($"{MessagesPath(organizationId, channelId)}?limit=100", Alice));
        var defaultPage = await ReadJsonAsync(await GetAsync(MessagesPath(organizationId, channelId), Alice));

        body["items"]!.AsArray().Should().HaveCount(100);
        body["next"].Should().NotBeNull();
        defaultPage["items"]!.AsArray().Should().HaveCount(50);
    }

    [Theory]
    [InlineData("limit=0")]
    [InlineData("limit=101")]
    [InlineData("limit=-5")]
    public async Task List_WithAPageSizeOutOfRange_IsRefusedAsync(string query)
    {
        var (organizationId, channelId) = await ChannelAsync();

        using var response = await GetAsync($"{MessagesPath(organizationId, channelId)}?{query}", Alice);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadJsonAsync(response))["code"]!.GetValue<string>().Should().Be("messaging.page_invalid");
    }

    [Fact]
    public async Task List_WithBeforeAndAfter_IsRefusedAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();

        using var response = await GetAsync($"{MessagesPath(organizationId, channelId)}?before=5&after=3", Alice);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadJsonAsync(response))["code"]!.GetValue<string>().Should().Be("messaging.page_invalid");
    }

    [Theory]
    [InlineData("before=abc")]
    [InlineData("after=abc")]
    [InlineData("limit=abc")]
    public async Task List_WithAMalformedQuery_OnlyAMemberLearnsItIsBadAsync(string query)
    {
        var (organizationId, channelId) = await ChannelAsync();

        using var member = await GetAsync($"{MessagesPath(organizationId, channelId)}?{query}", Alice);
        using var someoneElse = await GetAsync($"{MessagesPath(organizationId, channelId)}?{query}", Bob);

        member.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        someoneElse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task List_ForAnUnknownChannelOrAChannelOfAnotherOrganization_IsNotFoundAsync()
    {
        var (mine, _) = await ChannelAsync();
        var (_, foreignChannel) = await ChannelAsync();
        await SendMessageAsync((await ChannelOwnerOrganizationAsync(foreignChannel)), foreignChannel, Alice, "elsewhere secret");

        using var unknown = await GetAsync(MessagesPath(mine, Guid.CreateVersion7()), Alice);
        using var foreign = await GetAsync(MessagesPath(mine, foreignChannel), Alice);

        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        foreign.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadJsonAsync(foreign))["code"]!.GetValue<string>().Should().Be("messaging.conversation_not_found");
        (await foreign.Content.ReadAsStringAsync(CancellationToken)).Should().NotContain("elsewhere secret");
    }

    [Fact]
    public async Task List_AsSomeoneElse_IsNotFoundAndShowsNoMessagesAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        await SendMessageAsync(organizationId, channelId, Alice, "members only");

        using var response = await GetAsync(MessagesPath(organizationId, channelId), Bob);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync(CancellationToken)).Should().NotContain("members only");
    }

    [Fact]
    public async Task List_AfterTheMembershipEnded_IsNotFoundAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        await AddMemberAsync(organizationId, Bob, SystemRoles.Member);
        await SendMessageAsync(organizationId, channelId, Alice, "visible while a member");
        using (var before = await GetAsync(MessagesPath(organizationId, channelId), Bob))
        {
            before.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        await ExecuteAsync("UPDATE organization.members SET status = 'removed' WHERE org_id = $1 AND account_id = $2", organizationId, await AccountAsync(Bob));

        using var after = await GetAsync(MessagesPath(organizationId, channelId), Bob);

        after.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task List_KeepsADeletedMessageAsATombstoneWithoutItsTextAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        var ids = await SendManyAsync(organizationId, channelId, 3);
        await DeleteAsync(channelId, ids[1]);

        var body = await ReadJsonAsync(await GetAsync(MessagesPath(organizationId, channelId), Alice));

        Ids(body).Should().Equal(ids[2], ids[1], ids[0]);
        body["items"]![1]!["text"].Should().BeNull();
        body["items"]![1]!["deletedAt"].Should().NotBeNull();
    }

    [Fact]
    public async Task Messages_AcrossOrganizations_AreIsolatedByRowLevelSecurityAsync()
    {
        var (first, firstChannel) = await ChannelAsync();
        var (second, secondChannel) = await ChannelAsync();
        await SendMessageAsync(first, firstChannel, Alice, "in the first organization");
        await SendMessageAsync(second, secondChannel, Alice, "in the second organization");

        var firstBody = await ReadJsonAsync(await GetAsync(MessagesPath(first, firstChannel), Alice));
        var secondBody = await ReadJsonAsync(await GetAsync(MessagesPath(second, secondChannel), Alice));

        firstBody.ToJsonString().Should().Contain("in the first organization").And.NotContain("second organization");
        secondBody.ToJsonString().Should().Contain("in the second organization").And.NotContain("first organization");
        (await PostedEventsAsync(first)).Should().ContainSingle();
        (await PostedEventsAsync(second)).Should().ContainSingle();
    }

    [Fact]
    public async Task Logs_NeverHoldMessageTextOrChannelNamesAsync()
    {
        const string TextMarker = "TEXT-MARKER-7f3a9c";
        const string NameMarker = "NAME-MARKER-91bc4e";
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(organizationId, Alice, NameMarker);
        var clientMessageId = Guid.NewGuid();

        await SendMessageAsync(organizationId, channelId, Alice, TextMarker, clientMessageId);
        await SendMessageAsync(organizationId, channelId, Alice, TextMarker, clientMessageId);
        using var invalid = await PostAsync(MessagesPath(organizationId, channelId), Alice, new { clientMessageId = Guid.NewGuid(), text = TextMarker + "\a" });
        using var taken = await PostAsync($"/api/v1/orgs/{organizationId}/channels", Alice, new { name = NameMarker.ToLowerInvariant() });
        Publisher.Fail = true;
        await SendMessageAsync(organizationId, channelId, Alice, TextMarker + " again");
        using var list = await GetAsync(MessagesPath(organizationId, channelId), Alice);
        using var channels = await GetAsync($"/api/v1/orgs/{organizationId}/channels", Alice);

        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        taken.StatusCode.Should().Be(HttpStatusCode.Conflict);
        Logs.Entries.Should().NotBeEmpty();
        Logs.Entries.Should().NotContain(entry => entry.Contains("MARKER", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<(Guid OrganizationId, Guid ChannelId)> ChannelAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        return (organizationId, await CreateChannelAsync(organizationId, Alice, "General"));
    }

    private async Task<Guid> ChannelOwnerOrganizationAsync(Guid channelId) =>
        (Guid)(await QuerySingleAsync("SELECT org_id FROM messaging.conversations WHERE id = $1", channelId))[0]!;

    private async Task<List<Guid>> SendManyAsync(Guid organizationId, Guid channelId, int count)
    {
        var ids = new List<Guid>();
        for (var index = 0; index < count; index++)
        {
            ids.Add((await SendMessageAsync(organizationId, channelId, Alice, $"message {index}"))["id"]!.GetValue<Guid>());
        }

        return ids;
    }

    // Creating a channel also writes its conversation and membership events; these tests are about the message.
    private async Task<List<(string Type, JsonNode Payload)>> PostedEventsAsync(Guid organizationId) =>
        [.. (await OutboxAsync(organizationId)).Where(entry => entry.Type == "messaging.message-posted.v1")];

    private async Task ShouldHaveStoredNothingAsync(Guid organizationId, Guid channelId)
    {
        (await QuerySingleAsync("SELECT count(*) FROM messaging.messages WHERE conversation_id = $1", channelId))[0].Should().Be(0L);
        (await PostedEventsAsync(organizationId)).Should().BeEmpty();
        Publisher.Messages.Should().BeEmpty();
    }

    private static async Task ShouldBeInvalidAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadJsonAsync(response))["code"]!.GetValue<string>().Should().Be("messaging.message_invalid");
    }

    private static string MessagesPath(Guid organizationId, Guid channelId) => $"/api/v1/orgs/{organizationId}/conversations/{channelId}/messages";

    private static List<Guid> Ids(JsonNode body) =>
        [.. body["items"]!.AsArray().Select(message => message!["id"]!.GetValue<Guid>())];
}
