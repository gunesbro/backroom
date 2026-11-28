using System.Net;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Backroom.Modules.Messaging.Contracts;
using Backroom.Modules.Permissions.Contracts;
using Backroom.Modules.Realtime.Contracts;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;

namespace Backroom.Modules.Messaging.IntegrationTests;

/// <summary>
/// Phase 2 step 9 (docs/contracts/phase-2.md, flow 2 "Reply with quote") with <c>Features:MessageReply</c> on: the reply names a message of
/// the same conversation, reads carry its quote (read from the original, never copied), the original author is notified, and a deleted
/// original shows nothing of itself.
/// </summary>
[Collection(CellCollection.Name)]
public sealed class MessageReplyTests(PostgresDatabase database, KeycloakRealm keycloak) : MessagingTestBase(database, keycloak)
{
    private const string Marker = "SECRETMARKER";

    protected override bool MessageEditEnabled => true;

    protected override bool MessageReplyEnabled => true;

    [Fact]
    public async Task Reply_ToAMessageOfTheConversation_CarriesTheQuoteAnnouncesItAndNotifiesTheAuthorAsync()
    {
        var (organizationId, channelId, bob, original) = await BobsMessageAsync("Hello there");
        var alice = await AccountAsync(Alice);

        using var response = await ReplyAsync(organizationId, channelId, Alice, original, "Answer");

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await ReadJsonAsync(response);
        var replyId = body["id"]!.GetValue<Guid>();
        var quote = body["replyTo"]!;
        quote["messageId"]!.GetValue<Guid>().Should().Be(original);
        quote["authorAccountId"]!.GetValue<Guid>().Should().Be(bob);
        quote["text"]!.GetValue<string>().Should().Be("Hello there");
        quote["deleted"]!.GetValue<bool>().Should().BeFalse();
        (await QuerySingleAsync("SELECT reply_to_message_id FROM messaging.messages WHERE id = $1", replyId)).Should().Equal(original);

        var live = Publisher.Messages[^1].Message.ReplyTo;
        live.Should().NotBeNull();
        live!.MessageId.Should().Be(original);
        live.Text.Should().Be("Hello there");

        using var history = await GetAsync($"/api/v1/orgs/{organizationId}/conversations/{channelId}/messages", Alice);
        var items = (await ReadJsonAsync(history))["items"]!.AsArray();
        items.Single(item => item!["id"]!.GetValue<Guid>() == replyId)!["replyTo"]!["text"]!.GetValue<string>().Should().Be("Hello there");
        items.Single(item => item!["id"]!.GetValue<Guid>() == original)!.AsObject().ContainsKey("replyTo").Should().BeFalse();

        var activity = (await OutboxAsync(organizationId)).Single(entry => entry.Type == ActivityAdded.EventType).Payload;
        activity["kind"]!.GetValue<string>().Should().Be(ActivityKinds.QuotedReply);
        activity["message_id"]!.GetValue<Guid>().Should().Be(replyId);
        activity["actor_account_id"]!.GetValue<Guid>().Should().Be(alice);
        activity["account_ids"]!.AsArray().Select(node => node!.GetValue<Guid>()).Should().Equal(bob);
        activity.ToJsonString().Should().NotContain("Hello there", "the event carries ids only");
    }

    [Fact]
    public async Task Reply_ToOneOwnMessage_NotifiesNobodyAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(organizationId, Alice, "General");
        var own = (await SendMessageAsync(organizationId, channelId, Alice, "mine"))["id"]!.GetValue<Guid>();

        using var response = await ReplyAsync(organizationId, channelId, Alice, own, "again");

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        (await OutboxAsync(organizationId)).Should().NotContain(entry => entry.Type == ActivityAdded.EventType);
    }

    [Fact]
    public async Task Reply_WhenTheAuthorCanNoLongerReadTheConversation_NotifiesNobodyAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddMemberAsync(organizationId, Bob, SystemRoles.Member);
        var bob = await AccountAsync(Bob);
        var privateChannel = await CreatePrivateChannelAsync(organizationId, Alice, "Secret", bob);
        var original = (await SendMessageAsync(organizationId, privateChannel, Bob, "bye"))["id"]!.GetValue<Guid>();
        using (var left = await PostAsync($"/api/v1/orgs/{organizationId}/conversations/{privateChannel}/leave", Bob, new { }))
        {
            left.EnsureSuccessStatusCode();
        }

        using var response = await ReplyAsync(organizationId, privateChannel, Alice, original, "after you left");

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        (await OutboxAsync(organizationId)).Should().NotContain(entry => entry.Type == ActivityAdded.EventType);
    }

    [Fact]
    [Trait("Category", "Privacy")]
    public async Task Reply_ToAMessageOfAnotherOrUnreadableConversationOrUnknown_IsTheSameRefusalAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddMemberAsync(organizationId, Bob, SystemRoles.Member);
        var general = await CreateChannelAsync(organizationId, Alice, "General");
        var other = await CreateChannelAsync(organizationId, Alice, "Other");
        var secret = await CreatePrivateChannelAsync(organizationId, Alice, "Secret");
        using (var joined = await PostAsync($"/api/v1/orgs/{organizationId}/conversations/{general}/join", Bob, new { }))
        {
            joined.EnsureSuccessStatusCode();
        }

        var inOther = (await SendMessageAsync(organizationId, other, Alice, "elsewhere"))["id"]!.GetValue<Guid>();
        var inSecret = (await SendMessageAsync(organizationId, secret, Alice, Marker))["id"]!.GetValue<Guid>();

        using var foreign = await ReplyAsync(organizationId, general, Bob, inOther, "x");
        using var unreadable = await ReplyAsync(organizationId, general, Bob, inSecret, "x");
        using var unknown = await ReplyAsync(organizationId, general, Bob, Guid.NewGuid(), "x");

        foreach (var refused in new[] { foreign, unreadable, unknown })
        {
            refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await ErrorCodeAsync(refused)).Should().Be("messaging.reply_invalid");
        }

        (await ReadJsonAsync(unreadable)).ToJsonString().Should().Be((await ReadJsonAsync(unknown)).ToJsonString());
        (await ReadJsonAsync(foreign)).ToJsonString().Should().Be((await ReadJsonAsync(unknown)).ToJsonString());
        (await QuerySingleAsync("SELECT count(*) FROM messaging.messages WHERE conversation_id = $1", general)).Should().Equal(0L);
    }

    [Fact]
    public async Task Reply_ToADeletedMessage_IsRefusedAsync()
    {
        var (organizationId, channelId, _, original) = await BobsMessageAsync("doomed");
        await DeleteViaApiAsync(organizationId, original, Bob);

        using var response = await ReplyAsync(organizationId, channelId, Alice, original, "too late");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCodeAsync(response)).Should().Be("messaging.reply_invalid");
        (await ReadJsonAsync(response))["reason"]!.GetValue<string>().Should().Be("reply.deleted");
    }

    [Fact]
    [Trait("Category", "Privacy")]
    public async Task Quote_WhenTheOriginalIsDeleted_ShowsNothingOfItsContentOrAuthorAsync()
    {
        var (organizationId, channelId, _, original) = await BobsMessageAsync(Marker);
        using var reply = await ReplyAsync(organizationId, channelId, Alice, original, "answer");
        var replyId = (await ReadJsonAsync(reply))["id"]!.GetValue<Guid>();
        await DeleteViaApiAsync(organizationId, original, Bob);

        using var history = await GetAsync($"/api/v1/orgs/{organizationId}/conversations/{channelId}/messages", Alice);
        var raw = await history.Content.ReadAsStringAsync(CancellationToken);
        var item = JsonNode.Parse(raw)!["items"]!.AsArray().Single(node => node!["id"]!.GetValue<Guid>() == replyId)!;
        var quote = item["replyTo"]!.AsObject();

        quote["messageId"]!.GetValue<Guid>().Should().Be(original);
        quote["deleted"]!.GetValue<bool>().Should().BeTrue();
        quote.Select(pair => pair.Key).Should().BeEquivalentTo(["messageId", "deleted"]);
        raw.Should().NotContain(Marker);
    }

    [Fact]
    public async Task Quote_WhenTheOriginalIsEdited_ShowsTheNewTextOnTheNextReadAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(organizationId, Alice, "General");
        var original = (await SendMessageAsync(organizationId, channelId, Alice, "first"))["id"]!.GetValue<Guid>();
        using var reply = await ReplyAsync(organizationId, channelId, Alice, original, "reply");
        var replyId = (await ReadJsonAsync(reply))["id"]!.GetValue<Guid>();
        using var edit = await SendAsync(
            HttpMethod.Patch, $"/api/v1/orgs/{organizationId}/messages/{original}", Alice,
            new { blocks = JsonNode.Parse("""[{"type":"paragraph","children":[{"type":"text","text":"second"}]}]""") });
        edit.EnsureSuccessStatusCode();

        using var history = await GetAsync($"/api/v1/orgs/{organizationId}/conversations/{channelId}/messages", Alice);

        var item = (await ReadJsonAsync(history))["items"]!.AsArray().Single(node => node!["id"]!.GetValue<Guid>() == replyId)!;
        item["replyTo"]!["text"]!.GetValue<string>().Should().Be("second");
    }

    [Fact]
    public async Task List_WithManyReplies_QuotesEachFromItsOwnOriginalAndCutsLongTextsAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(organizationId, Alice, "General");
        var first = (await SendMessageAsync(organizationId, channelId, Alice, "one"))["id"]!.GetValue<Guid>();
        var second = (await SendMessageAsync(organizationId, channelId, Alice, new string('a', 300)))["id"]!.GetValue<Guid>();
        (await ReplyAsync(organizationId, channelId, Alice, first, "r1")).Dispose();
        (await ReplyAsync(organizationId, channelId, Alice, second, "r2")).Dispose();
        (await ReplyAsync(organizationId, channelId, Alice, first, "r3")).Dispose();

        using var history = await GetAsync($"/api/v1/orgs/{organizationId}/conversations/{channelId}/messages", Alice);

        var quotes = (await ReadJsonAsync(history))["items"]!.AsArray()
            .Where(node => node!["replyTo"] is not null)
            .ToDictionary(node => node!["text"]!.GetValue<string>(), node => node!["replyTo"]!["text"]!.GetValue<string>());
        quotes.Should().Equal(new Dictionary<string, string> { ["r1"] = "one", ["r2"] = new string('a', 200), ["r3"] = "one" });
    }

    [Fact]
    public async Task Reply_RepeatedAfterTheOriginalWasDeleted_AnswersTheStoredMessageAsync()
    {
        var (organizationId, channelId, _, original) = await BobsMessageAsync("will go");
        var clientId = Guid.NewGuid();
        using var first = await ReplyAsync(organizationId, channelId, Alice, original, "answer", clientId);
        await DeleteViaApiAsync(organizationId, original, Bob);

        using var repeat = await ReplyAsync(organizationId, channelId, Alice, original, "answer", clientId);

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        repeat.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(repeat))["replyTo"]!["deleted"]!.GetValue<bool>().Should().BeTrue();
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

    private Task<HttpResponseMessage> ReplyAsync(Guid organizationId, Guid channelId, string username, Guid replyTo, string text, Guid? clientMessageId = null) =>
        PostAsync(
            $"/api/v1/orgs/{organizationId}/conversations/{channelId}/messages", username,
            new { clientMessageId = clientMessageId ?? Guid.NewGuid(), text, replyToMessageId = replyTo });

    private async Task DeleteViaApiAsync(Guid organizationId, Guid messageId, string username)
    {
        using var response = await SendAsync(HttpMethod.Delete, $"/api/v1/orgs/{organizationId}/messages/{messageId}", username, null);
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }
}
