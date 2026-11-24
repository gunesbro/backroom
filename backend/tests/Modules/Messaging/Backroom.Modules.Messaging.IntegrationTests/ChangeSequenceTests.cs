using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Npgsql;
using Backroom.Modules.Permissions.Contracts;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;

namespace Backroom.Modules.Messaging.IntegrationTests;

/// <summary>
/// Phase 2 step 5 (docs/contracts/phase-2.md, "Sequence numbers"; docs/adr/0011): the conversation's change counter,
/// <c>version_seq</c>, tombstones, the <c>changed_after</c> read and the cell epoch on every cursor. Edits and deletes
/// have no endpoint yet (step 9), so the tests make those changes the way that step will: the next change number
/// under the conversation row, written to the message.
/// </summary>
[Collection(CellCollection.Name)]
[Trait("Category", "Privacy")]
public sealed class ChangeSequenceTests(PostgresDatabase database, KeycloakRealm keycloak) : MessagingTestBase(database, keycloak)
{
    private static readonly string s_epoch = TestCellEpoch.Initial.ToString(CultureInfo.InvariantCulture);

    [Fact]
    public async Task Send_TakesTheNextChangeNumberAsSeqAndVersionSeq_AndAnswersWithTheEpochAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();

        var first = await SendMessageAsync(organizationId, channelId, Alice, "one");
        var second = await SendMessageAsync(organizationId, channelId, Alice, "two");

        first["seq"]!.GetValue<long>().Should().Be(1);
        first["versionSeq"]!.GetValue<long>().Should().Be(1);
        second["seq"]!.GetValue<long>().Should().Be(2);
        second["versionSeq"]!.GetValue<long>().Should().Be(2);
        second["epoch"]!.GetValue<string>().Should().Be(s_epoch);
        second["deletedAt"].Should().BeNull();
        (await QuerySingleAsync("SELECT change_seq, last_message_seq FROM messaging.conversations WHERE id = $1", channelId))
            .Should().Equal(2L, 2L);
        (await QuerySingleAsync("SELECT seq, version_seq FROM messaging.messages WHERE id = $1", second["id"]!.GetValue<Guid>()))
            .Should().Equal(2L, 2L);
        Publisher.Messages.Should().HaveCount(2, "without the flag the Phase 1 event is sent");
        Publisher.MessagesV2.Should().BeEmpty();
    }

    [Fact]
    public async Task Send_ARepeat_AnswersWithTheEpochAndMovesNoCounterAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        var clientMessageId = Guid.NewGuid();
        await SendMessageAsync(organizationId, channelId, Alice, "once", clientMessageId);

        var repeat = await SendMessageAsync(organizationId, channelId, Alice, "once", clientMessageId);

        repeat["epoch"]!.GetValue<string>().Should().Be(s_epoch);
        repeat["versionSeq"]!.GetValue<long>().Should().Be(1);
        (await QuerySingleAsync("SELECT change_seq, last_message_seq FROM messaging.conversations WHERE id = $1", channelId))
            .Should().Equal(1L, 1L);
    }

    [Fact]
    public async Task List_AnswersWithTheEpochAndEachVersionSeqAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        await SendManyAsync(organizationId, channelId, 2);

        var body = await ReadJsonAsync(await GetAsync(MessagesPath(organizationId, channelId), Alice));

        body["epoch"]!.GetValue<string>().Should().Be(s_epoch);
        body["items"]!.AsArray().Select(item => item!["versionSeq"]!.GetValue<long>()).Should().Equal(2, 1);
    }

    [Fact]
    public async Task ChangedAfter_ReturnsNewChangedAndDeletedMessagesInTheOrderOfTheChangesAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        var ids = await SendManyAsync(organizationId, channelId, 3);
        await EditAsync(channelId, ids[0], "edited");
        await DeleteAsync(channelId, ids[1]);

        var everything = await ReadJsonAsync(await GetAsync(MessagesPath(organizationId, channelId, "changed_after=0"), Alice));
        var sinceTheSends = await ReadJsonAsync(await GetAsync(MessagesPath(organizationId, channelId, "changed_after=3"), Alice));
        var caughtUp = await ReadJsonAsync(await GetAsync(MessagesPath(organizationId, channelId, "changed_after=5"), Alice));

        Ids(everything).Should().Equal(ids[2], ids[0], ids[1]);
        everything["items"]!.AsArray().Select(item => item!["versionSeq"]!.GetValue<long>()).Should().Equal(3, 4, 5);
        everything["items"]!.AsArray().Select(item => item!["seq"]!.GetValue<long>()).Should().Equal(3, 1, 2);
        everything["epoch"]!.GetValue<string>().Should().Be(s_epoch);
        everything["next"].Should().BeNull();
        Ids(sinceTheSends).Should().Equal(ids[0], ids[1]);
        sinceTheSends["items"]![0]!["text"]!.GetValue<string>().Should().Be("edited");
        sinceTheSends["items"]![1]!["deletedAt"].Should().NotBeNull();
        Ids(caughtUp).Should().BeEmpty();
        (await QuerySingleAsync("SELECT change_seq, last_message_seq FROM messaging.conversations WHERE id = $1", channelId))
            .Should().Equal(new object[] { 5L, 3L }, "edits and deletes move the change counter, never the newest message");
    }

    [Fact]
    public async Task ChangedAfter_PagesByVersionSeqUntilNothingIsLeftAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        var ids = await SendManyAsync(organizationId, channelId, 5);
        await EditAsync(channelId, ids[1], "edited");

        var seen = new List<Guid>();
        long? cursor = 0;
        var pages = 0;
        while (cursor is not null)
        {
            var page = await ReadJsonAsync(await GetAsync(MessagesPath(organizationId, channelId, $"changed_after={cursor}&limit=2"), Alice));
            page["items"]!.AsArray().Count.Should().BeLessThanOrEqualTo(2);
            seen.AddRange(Ids(page));
            cursor = page["next"]?.GetValue<long>();
            (++pages).Should().BeLessThan(10);
        }

        seen.Should().Equal(ids[0], ids[2], ids[3], ids[4], ids[1]);
        pages.Should().Be(3);
    }

    [Theory]
    [InlineData("changed_after=1")]
    [InlineData("changed_after=1&epoch=6")]
    [InlineData("changed_after=1&epoch=")]
    [InlineData("changed_after=1&epoch=07")]
    [InlineData("changed_after=1&epoch=123456789012345678901234567890123")]
    [InlineData("after=1&epoch=8")]
    [InlineData("before=2&epoch=8")]
    [InlineData("epoch=8")]
    public async Task List_WithAMissingOrDifferentEpoch_AsksForAResyncAsync(string query)
    {
        var (organizationId, channelId) = await ChannelAsync();
        await SendManyAsync(organizationId, channelId, 2);

        using var response = await GetAsync($"/api/v1/orgs/{organizationId}/conversations/{channelId}/messages?{query}", Alice);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadJsonAsync(response))["code"]!.GetValue<string>().Should().Be("messaging.resync_required");
    }

    [Theory]
    [InlineData("")]
    [InlineData("after=1")]
    [InlineData("before=2")]
    [InlineData("limit=1")]
    public async Task List_WithoutTheEpochWhileTheFlagIsOff_StillAnswersPhase1ReadsAsync(string query)
    {
        var (organizationId, channelId) = await ChannelAsync();
        await SendManyAsync(organizationId, channelId, 2);

        using var response = await GetAsync($"/api/v1/orgs/{organizationId}/conversations/{channelId}/messages?{query}", Alice);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(response))["epoch"]!.GetValue<string>().Should().Be(s_epoch);
    }

    [Theory]
    [InlineData("after=3", HttpStatusCode.OK)]
    [InlineData("after=4", HttpStatusCode.Conflict)]
    [InlineData("changed_after=3", HttpStatusCode.OK)]
    [InlineData("changed_after=4", HttpStatusCode.Conflict)]
    [InlineData("before=4", HttpStatusCode.OK)]
    [InlineData("before=5", HttpStatusCode.Conflict)]
    [InlineData("after=9223372036854775807", HttpStatusCode.Conflict)]
    [InlineData("before=9223372036854775807", HttpStatusCode.Conflict)]
    public async Task List_WithACursorAheadOfTheChangeCounter_AsksForAResyncAsync(string cursor, HttpStatusCode expected)
    {
        var (organizationId, channelId) = await ChannelAsync();
        await SendManyAsync(organizationId, channelId, 3);

        using var response = await GetAsync(MessagesPath(organizationId, channelId, cursor), Alice);

        response.StatusCode.Should().Be(expected);
        if (expected == HttpStatusCode.Conflict)
        {
            (await ReadJsonAsync(response))["code"]!.GetValue<string>().Should().Be("messaging.resync_required");
        }
    }

    [Theory]
    [InlineData("changed_after=1&after=1")]
    [InlineData("changed_after=1&before=2")]
    [InlineData("changed_after=-1")]
    [InlineData("changed_after=1&limit=101")]
    public async Task ChangedAfter_WithAnotherCursorOrOutOfRange_IsRefusedAsync(string query)
    {
        var (organizationId, channelId) = await ChannelAsync();
        await SendManyAsync(organizationId, channelId, 2);

        using var response = await GetAsync(MessagesPath(organizationId, channelId, query), Alice);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadJsonAsync(response))["code"]!.GetValue<string>().Should().Be("messaging.page_invalid");
    }

    [Fact]
    public async Task List_AfterTheEpochChanged_RefusesTheOldEpochAndAnswersWithTheNewOneAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        await SendManyAsync(organizationId, channelId, 2);
        CellEpoch.Epoch = TestCellEpoch.Initial + 1;

        using var stale = await GetAsync(MessagesPath(organizationId, channelId, "changed_after=1"), Alice);
        var fresh = await ReadJsonAsync(await GetAsync(
            $"/api/v1/orgs/{organizationId}/conversations/{channelId}/messages?changed_after=0&epoch={TestCellEpoch.Initial + 1}", Alice));
        var sent = await SendMessageAsync(organizationId, channelId, Alice, "after the failover");

        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        fresh["epoch"]!.GetValue<string>().Should().Be("8");
        Ids(fresh).Should().HaveCount(2);
        sent["epoch"]!.GetValue<string>().Should().Be("8");
    }

    [Fact]
    public async Task ReadAndSend_WhileTheEpochIsNotConfirmed_AreRefusedAndStoreNothingAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        CellEpoch.Epoch = null;

        using var read = await GetAsync($"/api/v1/orgs/{organizationId}/conversations/{channelId}/messages", Alice);
        using var send = await PostAsync(
            $"/api/v1/orgs/{organizationId}/conversations/{channelId}/messages", Alice, new { clientMessageId = Guid.NewGuid(), text = "not yet" });

        read.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await ReadJsonAsync(read))["code"]!.GetValue<string>().Should().Be("cell.epoch_unconfirmed");
        send.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await QuerySingleAsync("SELECT count(*) FROM messaging.messages WHERE conversation_id = $1", channelId))[0].Should().Be(0L);
        (await QuerySingleAsync("SELECT change_seq FROM messaging.conversations WHERE id = $1", channelId))[0].Should().Be(0L);
        Publisher.Messages.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("after=0")]
    [InlineData("before=4")]
    [InlineData("changed_after=0")]
    public async Task Tombstone_KeepsItsPlaceInEveryRead_WithoutContentAuthorOrClientIdAsync(string query)
    {
        var (organizationId, channelId) = await ChannelAsync();
        var ids = await SendManyAsync(organizationId, channelId, 3);
        await DeleteAsync(channelId, ids[1]);

        var body = await ReadJsonAsync(await GetAsync(MessagesPath(organizationId, channelId, query), Alice));

        Ids(body).Should().BeEquivalentTo(ids, "a tombstone stays so that every cursor stays valid");
        var tombstone = body["items"]!.AsArray().Single(item => item!["id"]!.GetValue<Guid>() == ids[1])!.AsObject();
        tombstone["seq"]!.GetValue<long>().Should().Be(2);
        tombstone["versionSeq"]!.GetValue<long>().Should().Be(4);
        tombstone["deletedAt"].Should().NotBeNull();
        tombstone["text"].Should().BeNull();
        tombstone["authorAccountId"].Should().BeNull();
        tombstone["clientMessageId"].Should().BeNull();
        body.ToJsonString().Should().NotContain("message 1", "the deleted text appears nowhere");
        var kept = body["items"]!.AsArray().Single(item => item!["id"]!.GetValue<Guid>() == ids[0])!.AsObject();
        kept["text"]!.GetValue<string>().Should().Be("message 0");
        kept["deletedAt"].Should().BeNull();
    }

    [Fact]
    public async Task Tombstone_WhenItsSendIsRepeated_AnswersWithoutTheContentAndStoresNothingAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        var clientMessageId = Guid.NewGuid();
        var sent = await SendMessageAsync(organizationId, channelId, Alice, "soon deleted", clientMessageId);
        await DeleteAsync(channelId, sent["id"]!.GetValue<Guid>());

        using var response = await PostAsync(
            $"/api/v1/orgs/{organizationId}/conversations/{channelId}/messages", Alice, new { clientMessageId, text = "soon deleted" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var repeat = await ReadJsonAsync(response);
        repeat["id"]!.GetValue<Guid>().Should().Be(sent["id"]!.GetValue<Guid>());
        repeat["text"].Should().BeNull();
        repeat["deletedAt"].Should().NotBeNull();
        (await QuerySingleAsync("SELECT count(*) FROM messaging.messages WHERE conversation_id = $1", channelId))[0].Should().Be(1L);
    }

    [Fact]
    public async Task Tombstone_CannotKeepItsContentInTheDatabaseAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        var id = (await SendManyAsync(organizationId, channelId, 1))[0];

        var kept = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync("UPDATE messaging.messages SET deleted_at = now() WHERE id = $1", id));
        var emptied = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync("UPDATE messaging.messages SET text = NULL, blocks = NULL WHERE id = $1", id));

        kept.ConstraintName.Should().Be("messages_tombstone_check");
        emptied.ConstraintName.Should().Be("messages_tombstone_check");
    }

    [Fact]
    public async Task ChangedAfter_AcrossOrganizations_NeverReachesAnotherOrganizationsConversationAsync()
    {
        var (first, firstChannel) = await ChannelAsync();
        var (second, secondChannel) = await ChannelAsync();
        await SendMessageAsync(first, firstChannel, Alice, "in the first organization");
        await SendMessageAsync(second, secondChannel, Alice, "in the second organization");
        await DeleteAsync(secondChannel, (Guid)(await QuerySingleAsync("SELECT id FROM messaging.messages WHERE conversation_id = $1", secondChannel))[0]!);

        var own = await ReadJsonAsync(await GetAsync(MessagesPath(first, firstChannel, "changed_after=0"), Alice));
        using var foreign = await GetAsync(MessagesPath(first, secondChannel, "changed_after=0"), Alice);
        // Whatever the cursor says, another organization's conversation is not found: no resync answer reveals it.
        using var foreignAhead = await GetAsync(MessagesPath(first, secondChannel, "changed_after=99"), Alice);
        using var foreignStale = await GetAsync($"/api/v1/orgs/{first}/conversations/{secondChannel}/messages?changed_after=0&epoch=1", Alice);

        own["items"]!.AsArray().Should().ContainSingle().Which!["text"]!.GetValue<string>().Should().Be("in the first organization");
        foreach (var response in new[] { foreign, foreignAhead, foreignStale })
        {
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await ReadJsonAsync(response))["code"]!.GetValue<string>().Should().Be("messaging.conversation_not_found");
        }
    }

    [Fact]
    public async Task ChangedAfter_ForAPrivateConversationOfOthers_IsNotFoundWhateverTheCursorAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        await AddMemberAsync(organizationId, Bob, SystemRoles.Admin);
        await SendMessageAsync(organizationId, channelId, Alice, "private");
        await ExecuteAsync("UPDATE messaging.conversations SET kind = 'private_channel' WHERE id = $1", channelId);

        using var read = await GetAsync(MessagesPath(organizationId, channelId, "changed_after=0"), Bob);
        using var ahead = await GetAsync(MessagesPath(organizationId, channelId, "changed_after=99"), Bob);
        using var stale = await GetAsync($"/api/v1/orgs/{organizationId}/conversations/{channelId}/messages?changed_after=0&epoch=1", Bob);

        read.StatusCode.Should().Be(HttpStatusCode.NotFound);
        ahead.StatusCode.Should().Be(HttpStatusCode.NotFound, "the counter of a conversation the caller cannot read is not probed");
        stale.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ConcurrentSends_AreNumberedInCommitOrder_SoACursorReaderMissesNothingAsync()
    {
        const int Senders = 48;
        var (organizationId, channelId) = await ChannelAsync();
        // Warm the token cache: the sends below must overlap, not queue behind a sign-in.
        await SendMessageAsync(organizationId, channelId, Alice, "first");

        var sends = Enumerable.Range(0, Senders)
            .Select(index => Task.Run(() => SendMessageAsync(organizationId, channelId, Alice, $"concurrent {index}")))
            .ToArray();
        var all = Task.WhenAll(sends);

        // A reader that only ever asks for what changed after the last number it saw, while the sends commit. If a number
        // became visible before a lower one had committed, the reader would step over the lower one for good.
        var seen = new List<long>();
        long cursor = 0;
        var finished = false;
        while (!finished)
        {
            // One more pass after the last send has committed.
            finished = all.IsCompleted;
            long? next = cursor;
            while (next is not null)
            {
                var page = await ReadJsonAsync(await GetAsync(MessagesPath(organizationId, channelId, $"changed_after={next}&limit=7"), Alice));
                var numbers = page["items"]!.AsArray().Select(item => item!["versionSeq"]!.GetValue<long>()).ToList();
                seen.AddRange(numbers);
                cursor = numbers.Count > 0 ? numbers[^1] : cursor;
                next = page["next"]?.GetValue<long>();
            }
        }

        var sent = (await all).Select(message => message["seq"]!.GetValue<long>()).ToList();
        sent.Should().OnlyHaveUniqueItems();
        seen.Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        seen.Should().BeEquivalentTo(sent.Append(1L), "every committed message was reached by its cursor");
        var newest = sent.Max();
        (await QuerySingleAsync("SELECT change_seq, last_message_seq FROM messaging.conversations WHERE id = $1", channelId))
            .Should().Equal(newest, newest);
        (await QuerySingleAsync("SELECT count(*) FROM messaging.messages WHERE conversation_id = $1 AND version_seq <> seq", channelId))[0].Should().Be(0L);
    }

    private static string MessagesPath(Guid organizationId, Guid channelId, string? query = null) =>
        string.IsNullOrEmpty(query)
            ? $"/api/v1/orgs/{organizationId}/conversations/{channelId}/messages"
            : $"/api/v1/orgs/{organizationId}/conversations/{channelId}/messages?{query}&epoch={s_epoch}";

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
