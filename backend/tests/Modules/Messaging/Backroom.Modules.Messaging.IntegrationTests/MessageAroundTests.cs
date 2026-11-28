using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Backroom.Modules.Permissions.Contracts;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;

namespace Backroom.Modules.Messaging.IntegrationTests;

/// <summary>
/// Jump to a message (docs/contracts/phase-2.md, "Jump to a message"): <c>GET .../messages?around=</c> answers the message
/// and the messages around it with a cursor for each direction. The message with index i has <c>seq</c> i + 1.
/// </summary>
[Collection(CellCollection.Name)]
public sealed class MessageAroundTests(PostgresDatabase database, KeycloakRealm keycloak) : MessagingTestBase(database, keycloak)
{
    private static readonly string s_epoch = TestCellEpoch.Initial.ToString(CultureInfo.InvariantCulture);

    protected override bool MessageReplyEnabled => true;

    [Fact]
    public async Task Around_InTheMiddle_AnswersTheAnchorWithHalfOlderAndHalfNewerNewestFirstAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        var ids = await SendManyAsync(organizationId, channelId, 20);

        var body = await AroundAsync(organizationId, channelId, ids[10], "limit=7");

        Seqs(body).Should().Equal(14, 13, 12, 11, 10, 9, 8);
        Ids(body)[3].Should().Be(ids[10]);
        body["anchor"]!["id"]!.GetValue<Guid>().Should().Be(ids[10]);
        body["anchor"]!["seq"]!.GetValue<long>().Should().Be(11);
        body["before"]!.GetValue<long>().Should().Be(8);
        body["after"]!.GetValue<long>().Should().Be(14);
        body["epoch"]!.GetValue<string>().Should().Be(s_epoch);
        body["items"]![3]!["text"]!.GetValue<string>().Should().Be("message 10");
    }

    [Fact]
    public async Task Around_WithAnEvenNumberOfOthers_GivesTheExtraPlaceToTheNewerSideAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        var ids = await SendManyAsync(organizationId, channelId, 20);

        var body = await AroundAsync(organizationId, channelId, ids[10], "limit=6");

        Seqs(body).Should().Equal(14, 13, 12, 11, 10, 9);
    }

    [Fact]
    public async Task Around_NearTheStart_FillsWithNewerMessagesAndHasNoOlderCursorAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        var ids = await SendManyAsync(organizationId, channelId, 20);

        var body = await AroundAsync(organizationId, channelId, ids[1], "limit=9");

        Seqs(body).Should().Equal(9, 8, 7, 6, 5, 4, 3, 2, 1);
        body["before"].Should().BeNull();
        body["after"]!.GetValue<long>().Should().Be(9);
    }

    [Fact]
    public async Task Around_NearTheEnd_FillsWithOlderMessagesAndHasNoNewerCursorAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        var ids = await SendManyAsync(organizationId, channelId, 20);

        var body = await AroundAsync(organizationId, channelId, ids[18], "limit=9");

        Seqs(body).Should().Equal(20, 19, 18, 17, 16, 15, 14, 13, 12);
        body["after"].Should().BeNull();
        body["before"]!.GetValue<long>().Should().Be(12);
    }

    [Fact]
    public async Task Around_WhenTheWholeConversationFits_HasNoCursorsAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        var ids = await SendManyAsync(organizationId, channelId, 3);

        var body = await AroundAsync(organizationId, channelId, ids[1], null);

        Seqs(body).Should().Equal(3, 2, 1);
        body["before"].Should().BeNull();
        body["after"].Should().BeNull();
    }

    [Fact]
    public async Task Around_WithALimitOfOne_AnswersTheAnchorAndCursorsThatContinueFromItAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        var ids = await SendManyAsync(organizationId, channelId, 5);

        var body = await AroundAsync(organizationId, channelId, ids[2], "limit=1");

        Seqs(body).Should().Equal(3);
        body["before"]!.GetValue<long>().Should().Be(3);
        body["after"]!.GetValue<long>().Should().Be(3);
    }

    [Fact]
    public async Task Around_TheCursors_ContinueWithBeforeAndAfterWithoutGapsOrOverlapAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        var ids = await SendManyAsync(organizationId, channelId, 30);
        var body = await AroundAsync(organizationId, channelId, ids[14], "limit=5");
        var epoch = body["epoch"]!.GetValue<string>();
        var seen = Seqs(body);

        var older = body["before"]!.GetValue<long>();
        while (true)
        {
            var page = await ReadJsonAsync(await GetAsync($"{Path(organizationId, channelId)}?before={older}&epoch={epoch}&limit=4", Alice));
            seen.AddRange(Seqs(page));
            if (page["next"] is not { } next)
            {
                break;
            }

            older = next.GetValue<long>();
        }

        var newer = body["after"]!.GetValue<long>();
        while (true)
        {
            var page = await ReadJsonAsync(await GetAsync($"{Path(organizationId, channelId)}?after={newer}&epoch={epoch}&limit=4", Alice));
            seen.AddRange(Seqs(page));
            if (page["next"] is not { } next)
            {
                break;
            }

            newer = next.GetValue<long>();
        }

        seen.Should().OnlyHaveUniqueItems().And.BeEquivalentTo(Enumerable.Range(1, 30).Select(number => (long)number));
    }

    [Fact]
    public async Task Around_AtTheMaximumLimit_AnswersFortyNineOlderTheAnchorAndFiftyNewerAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        var ids = await SendManyAsync(organizationId, channelId, 150);

        var body = await AroundAsync(organizationId, channelId, ids[75], "limit=100");

        Seqs(body).Should().Equal(Enumerable.Range(27, 100).Reverse().Select(number => (long)number));
        body["before"]!.GetValue<long>().Should().Be(27);
        body["after"]!.GetValue<long>().Should().Be(126);
    }

    [Fact]
    public async Task Around_ADeletedAnchor_IsItsTombstoneInPlaceAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        var ids = await SendManyAsync(organizationId, channelId, 5);
        await DeleteAsync(channelId, ids[2]);

        var body = await AroundAsync(organizationId, channelId, ids[2], "limit=5");

        Seqs(body).Should().Equal(5, 4, 3, 2, 1);
        var tombstone = body["items"]![2]!.AsObject();
        tombstone["id"]!.GetValue<Guid>().Should().Be(ids[2]);
        tombstone["deletedAt"].Should().NotBeNull();
        tombstone["text"].Should().BeNull();
        tombstone["authorAccountId"].Should().BeNull();
        tombstone["clientMessageId"].Should().BeNull();
        body["anchor"]!["seq"]!.GetValue<long>().Should().Be(3);
        body.ToJsonString().Should().NotContain("message 2");
    }

    [Fact]
    public async Task Around_CarriesTheQuoteOfAReplyAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        var ids = await SendManyAsync(organizationId, channelId, 3);
        using var reply = await PostAsync(
            $"{Path(organizationId, channelId)}", Alice, new { clientMessageId = Guid.NewGuid(), text = "a reply", replyToMessageId = ids[0] });
        reply.EnsureSuccessStatusCode();

        var body = await AroundAsync(organizationId, channelId, ids[1], "limit=10");

        var quoted = body["items"]!.AsArray().Single(item => item!["text"]?.GetValue<string>() == "a reply")!;
        quoted["replyTo"]!["messageId"]!.GetValue<Guid>().Should().Be(ids[0]);
        quoted["replyTo"]!["text"]!.GetValue<string>().Should().Be("message 0");
    }

    [Theory]
    [InlineData("before=2")]
    [InlineData("after=1")]
    [InlineData("changed_after=0")]
    [InlineData("limit=0")]
    [InlineData("limit=101")]
    [InlineData("limit=-1")]
    public async Task Around_WithAnotherCursorOrALimitOutOfRange_IsRefusedAsync(string query)
    {
        var (organizationId, channelId) = await ChannelAsync();
        var ids = await SendManyAsync(organizationId, channelId, 3);

        using var response = await GetAsync($"{Path(organizationId, channelId)}?around={ids[1]}&{query}&epoch={s_epoch}", Alice);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCodeAsync(response)).Should().Be("messaging.page_invalid");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    public async Task Around_AtTheLimitBounds_AnswersAtMostTheLimitAsync(int limit)
    {
        var (organizationId, channelId) = await ChannelAsync();
        var ids = await SendManyAsync(organizationId, channelId, 12);

        var body = await AroundAsync(organizationId, channelId, ids[5], $"limit={limit}");

        Seqs(body).Should().HaveCount(Math.Min(limit, 12));
    }

    // Privacy rule: no existence oracle. An unknown message, a message of another conversation or organization, and a
    // conversation the caller cannot read all answer the same 404.
    [Fact]
    [Trait("Category", "Privacy")]
    public async Task Around_WhenTheMessageIsUnknownForeignOrUnreadable_AnswersTheSameNotFoundAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        var otherChannel = await CreateChannelAsync(organizationId, Alice, "Other");
        var inOther = (await SendManyAsync(organizationId, otherChannel, 1))[0];
        await SendManyAsync(organizationId, channelId, 2);
        await AddMemberAsync(organizationId, Bob, SystemRoles.Admin);
        var secret = await CreatePrivateChannelAsync(organizationId, Alice, "Secret");
        var inSecret = (await SendManyAsync(organizationId, secret, 1))[0];
        var (foreignOrganization, foreignChannel) = await ChannelAsync();
        var inForeign = (await SendManyAsync(foreignOrganization, foreignChannel, 1))[0];

        var unknown = await NotFoundAsync(organizationId, channelId, Guid.NewGuid(), Alice);
        var otherConversation = await NotFoundAsync(organizationId, channelId, inOther, Alice);
        var foreignOrg = await NotFoundAsync(organizationId, channelId, inForeign, Alice);
        var unreadable = await NotFoundAsync(organizationId, secret, inSecret, Bob);
        var unreadableWrongMessage = await NotFoundAsync(organizationId, secret, Guid.NewGuid(), Bob);
        var unknownConversation = await NotFoundAsync(organizationId, Guid.NewGuid(), inSecret, Bob);

        foreach (var body in new[] { otherConversation, foreignOrg, unreadable, unreadableWrongMessage, unknownConversation })
        {
            body.Should().Be(unknown, "the answers are identical whatever the reason");
        }

        unknown.Should().Contain("messaging.message_not_found");
    }

    [Fact]
    [Trait("Category", "Privacy")]
    public async Task Around_ForAnUnreadableConversation_IsNotFoundWhateverTheEpochAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        var ids = await SendManyAsync(organizationId, channelId, 3);
        await AddMemberAsync(organizationId, Bob, SystemRoles.Admin);
        await ExecuteAsync("UPDATE messaging.conversations SET kind = 'private_channel' WHERE id = $1", channelId);

        using var stale = await GetAsync($"{Path(organizationId, channelId)}?around={ids[1]}&epoch=1", Bob);

        stale.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ErrorCodeAsync(stale)).Should().Be("messaging.message_not_found");
    }

    [Theory]
    [InlineData("epoch=6")]
    [InlineData("epoch=")]
    [InlineData("epoch=123456789012345678901234567890123")]
    public async Task Around_WithADifferentEpoch_AsksForAResyncAsync(string epoch)
    {
        var (organizationId, channelId) = await ChannelAsync();
        var ids = await SendManyAsync(organizationId, channelId, 3);

        using var response = await GetAsync($"{Path(organizationId, channelId)}?around={ids[1]}&{epoch}", Alice);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ErrorCodeAsync(response)).Should().Be("messaging.resync_required");
    }

    [Fact]
    public async Task Around_WithoutAnEpoch_Works_AndAfterAFailoverTheOldEpochNeedsAResyncAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        var ids = await SendManyAsync(organizationId, channelId, 3);
        CellEpoch.Epoch = TestCellEpoch.Initial + 1;

        using var stale = await GetAsync($"{Path(organizationId, channelId)}?around={ids[1]}&epoch={s_epoch}", Alice);
        var fresh = await ReadJsonAsync(await GetAsync($"{Path(organizationId, channelId)}?around={ids[1]}", Alice));

        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        fresh["epoch"]!.GetValue<string>().Should().Be((TestCellEpoch.Initial + 1).ToString(CultureInfo.InvariantCulture));
        Seqs(fresh).Should().Equal(3, 2, 1);
    }

    [Fact]
    public async Task Around_WhileTheEpochIsNotConfirmed_AnswersServiceUnavailableAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        var ids = await SendManyAsync(organizationId, channelId, 2);
        CellEpoch.Epoch = null;

        using var response = await GetAsync($"{Path(organizationId, channelId)}?around={ids[0]}", Alice);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await ErrorCodeAsync(response)).Should().Be("cell.epoch_unconfirmed");
    }

    private static string Path(Guid organizationId, Guid channelId) => $"/api/v1/orgs/{organizationId}/conversations/{channelId}/messages";

    private async Task<JsonNode> AroundAsync(Guid organizationId, Guid channelId, Guid messageId, string? query)
    {
        using var response = await GetAsync($"{Path(organizationId, channelId)}?around={messageId}{(query is null ? "" : "&" + query)}", Alice);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await ReadJsonAsync(response);
    }

    private async Task<string> NotFoundAsync(Guid organizationId, Guid channelId, Guid messageId, string username)
    {
        using var response = await GetAsync($"{Path(organizationId, channelId)}?around={messageId}", username);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        return await response.Content.ReadAsStringAsync(CancellationToken);
    }

    private static List<long> Seqs(JsonNode body) => [.. body["items"]!.AsArray().Select(item => item!["seq"]!.GetValue<long>())];

    private static List<Guid> Ids(JsonNode body) => [.. body["items"]!.AsArray().Select(item => item!["id"]!.GetValue<Guid>())];

    private async Task<(Guid OrganizationId, Guid ChannelId)> ChannelAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        return (organizationId, await CreateChannelAsync(organizationId, Alice, "General"));
    }

    private async Task<List<Guid>> SendManyAsync(Guid organizationId, Guid channelId, int count)
    {
        var ids = new List<Guid>();
        for (var index = 0; index < count; index++)
        {
            ids.Add((await SendMessageAsync(organizationId, channelId, Alice, $"message {index}"))["id"]!.GetValue<Guid>());
        }

        return ids;
    }
}
