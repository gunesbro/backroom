using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Npgsql;
using Backroom.Modules.Messaging.Contracts;
using Backroom.Modules.Permissions.Contracts;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;

namespace Backroom.Modules.Messaging.IntegrationTests;

/// <summary>
/// More of flow 2 "Reply with quote": the race with a delete, replies that are deleted or edited, every read path, another organization,
/// and a reply that also mentions the original author.
/// </summary>
[Collection(CellCollection.Name)]
public sealed class MessageReplyRaceAndReadTests(PostgresDatabase database, KeycloakRealm keycloak) : MessagingTestBase(database, keycloak)
{
    private const string Marker = "SECRETMARKER";

    protected override bool MessageEditEnabled => true;

    protected override bool MessageReplyEnabled => true;

    protected override bool RichTextEnabled => true;

    [Fact]
    public async Task Reply_WhenTheOriginalIsDeletedWhileTheSendWaitsForTheRowLock_IsRefusedAndAnnouncesNothingAsync()
    {
        var (organizationId, channelId, _, original) = await BobsMessageAsync("will vanish");
        var before = await QuerySingleAsync("SELECT last_message_seq FROM messaging.conversations WHERE id = $1", channelId);
        Task<HttpResponseMessage> sending;
        await using (var holder = new NpgsqlConnection(Database.SuperuserConnectionString))
        {
            await holder.OpenAsync(CancellationToken);
            await using var transaction = await holder.BeginTransactionAsync(CancellationToken);
            await using (var take = new NpgsqlCommand("SELECT 1 FROM messaging.conversations WHERE id = $1 FOR UPDATE", holder, transaction))
            {
                take.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = channelId });
                await take.ExecuteScalarAsync(CancellationToken);
            }

            // The send reads the live original, then waits for the conversation row lock.
            sending = Task.Run(() => ReplyAsync(organizationId, channelId, Alice, original, "racing"), CancellationToken);
            var waited = Stopwatch.StartNew();
            while ((long)(await QuerySingleAsync("SELECT count(*) FROM pg_locks WHERE NOT granted AND locktype IN ('transactionid', 'tuple')"))[0]! < 1)
            {
                waited.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30), "the send should reach the conversation row lock");
                await Task.Delay(TimeSpan.FromMilliseconds(50), CancellationToken);
            }

            await using (var delete = new NpgsqlCommand(
                "UPDATE messaging.messages SET text = NULL, blocks = NULL, deleted_at = now() WHERE id = $1", holder, transaction))
            {
                delete.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = original });
                await delete.ExecuteNonQueryAsync(CancellationToken);
            }

            await transaction.CommitAsync(CancellationToken);
        }

        using var response = await sending;

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadJsonAsync(response))["reason"]!.GetValue<string>().Should().Be("reply.deleted");
        (await QuerySingleAsync("SELECT last_message_seq FROM messaging.conversations WHERE id = $1", channelId)).Should().Equal(before);
        (await QuerySingleAsync("SELECT count(*) FROM messaging.messages WHERE conversation_id = $1 AND reply_to_message_id IS NOT NULL", channelId)).Should().Equal(0L);
        Publisher.Messages.Should().HaveCount(1, "only the original was announced");
        (await OutboxAsync(organizationId)).Should().NotContain(entry => entry.Type == ActivityAdded.EventType);
    }

    [Fact]
    public async Task Delete_OfAReply_ClearsItsReferenceAndLeavesATombstoneAsync()
    {
        var (organizationId, channelId, _, original) = await BobsMessageAsync("quoted");
        using var reply = await ReplyAsync(organizationId, channelId, Alice, original, "answer");
        var replyId = (await ReadJsonAsync(reply))["id"]!.GetValue<Guid>();

        using (var deleted = await SendAsync(HttpMethod.Delete, $"/api/v1/orgs/{organizationId}/messages/{replyId}", Alice, null))
        {
            deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        (await QuerySingleAsync("SELECT reply_to_message_id IS NULL, deleted_at IS NOT NULL FROM messaging.messages WHERE id = $1", replyId)).Should().Equal(true, true);
        using var history = await GetAsync($"/api/v1/orgs/{organizationId}/conversations/{channelId}/messages", Alice);
        var item = (await ReadJsonAsync(history))["items"]!.AsArray().Single(node => node!["id"]!.GetValue<Guid>() == replyId)!;
        item.AsObject().ContainsKey("replyTo").Should().BeFalse();
    }

    [Fact]
    public async Task Edit_OfAReply_KeepsItsQuoteAsync()
    {
        var (organizationId, channelId, bob, original) = await BobsMessageAsync("quoted");
        using var reply = await ReplyAsync(organizationId, channelId, Alice, original, "answer");
        var replyId = (await ReadJsonAsync(reply))["id"]!.GetValue<Guid>();

        using var edit = await SendAsync(
            HttpMethod.Patch, $"/api/v1/orgs/{organizationId}/messages/{replyId}", Alice,
            new { blocks = JsonNode.Parse("""[{"type":"paragraph","children":[{"type":"text","text":"better answer"}]}]""") });

        edit.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadJsonAsync(edit);
        body["text"]!.GetValue<string>().Should().Be("better answer");
        body["replyTo"]!["messageId"]!.GetValue<Guid>().Should().Be(original);
        body["replyTo"]!["authorAccountId"]!.GetValue<Guid>().Should().Be(bob);
        body["replyTo"]!["text"]!.GetValue<string>().Should().Be("quoted");
    }

    [Fact]
    public async Task Read_ByAfterBeforeAndChangedAfter_CarriesTheQuoteAsync()
    {
        var (organizationId, channelId, _, original) = await BobsMessageAsync("quoted");
        using var reply = await ReplyAsync(organizationId, channelId, Alice, original, "answer");
        var sent = await ReadJsonAsync(reply);
        var replyId = sent["id"]!.GetValue<Guid>();
        var epoch = sent["epoch"]!.GetValue<string>();
        var path = $"/api/v1/orgs/{organizationId}/conversations/{channelId}/messages";
        var next = (long)(await QuerySingleAsync("SELECT change_seq FROM messaging.conversations WHERE id = $1", channelId))[0]! + 1;

        foreach (var query in new[] { "?after=0", $"?before={next}", $"?epoch={epoch}&changed_after=0" })
        {
            using var page = await GetAsync(path + query, Alice);
            page.StatusCode.Should().Be(HttpStatusCode.OK, query);
            var item = (await ReadJsonAsync(page))["items"]!.AsArray().Single(node => node!["id"]!.GetValue<Guid>() == replyId)!;
            item["replyTo"]!["text"]!.GetValue<string>().Should().Be("quoted", query);
        }
    }

    [Fact]
    [Trait("Category", "Privacy")]
    public async Task Reply_ToAMessageOfAnotherOrganization_IsTheSameRefusalAsUnknownAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(organizationId, Alice, "General");
        var otherOrganization = await ProvisionForAsync(Alice);
        var otherChannel = await CreateChannelAsync(otherOrganization, Alice, "General");
        var foreign = (await SendMessageAsync(otherOrganization, otherChannel, Alice, Marker))["id"]!.GetValue<Guid>();

        using var response = await ReplyAsync(organizationId, channelId, Alice, foreign, "x");
        using var unknown = await ReplyAsync(organizationId, channelId, Alice, Guid.NewGuid(), "x");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadJsonAsync(response))["reason"]!.GetValue<string>().Should().Be("reply.not_found");
        (await ReadJsonAsync(response)).ToJsonString().Should().Be((await ReadJsonAsync(unknown)).ToJsonString());
    }

    [Fact]
    public async Task Reply_ThatAlsoMentionsTheOriginalAuthor_GetsTheMentionNotAQuotedReplyItemAsync()
    {
        var (organizationId, channelId, bob, original) = await BobsMessageAsync("quoted");

        using var response = await PostAsync(
            $"/api/v1/orgs/{organizationId}/conversations/{channelId}/messages", Alice,
            new
            {
                clientMessageId = Guid.NewGuid(),
                replyToMessageId = original,
                blocks = JsonNode.Parse($$"""[{"type":"paragraph","children":[{"type":"mention","accountId":"{{bob}}"},{"type":"text","text":" agreed"}]}]"""),
            });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        (await OutboxAsync(organizationId)).Should().NotContain(entry => entry.Type == ActivityAdded.EventType);
        (await QuerySingleAsync("SELECT count(*) FROM messaging.message_mentions WHERE account_id = $1", bob)).Should().Equal(1L);
    }

    private async Task<(Guid OrganizationId, Guid ChannelId, Guid Bob, Guid Original)> BobsMessageAsync(string text)
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddMemberAsync(organizationId, Bob, SystemRoles.Member);
        var channelId = await CreateChannelAsync(organizationId, Alice, "General");
        using (var joined = await PostAsync($"/api/v1/orgs/{organizationId}/conversations/{channelId}/join", Bob, new { }))
        {
            joined.EnsureSuccessStatusCode();
        }

        var sent = await SendMessageAsync(organizationId, channelId, Bob, text);
        return (organizationId, channelId, await AccountAsync(Bob), sent["id"]!.GetValue<Guid>());
    }

    private Task<HttpResponseMessage> ReplyAsync(Guid organizationId, Guid channelId, string username, Guid replyTo, string text) =>
        PostAsync(
            $"/api/v1/orgs/{organizationId}/conversations/{channelId}/messages", username,
            new { clientMessageId = Guid.NewGuid(), text, replyToMessageId = replyTo });
}
