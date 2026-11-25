using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using MessagePack;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.Modules.Permissions.Contracts;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;

namespace Backroom.Modules.Messaging.IntegrationTests;

/// <summary>
/// Rich text (docs/contracts/phase-2.md, flow 2) with <c>Features:RichText</c> and <c>Features:MessageSync</c> on: a
/// send carries blocks in the closed schema; the server stores their canonical form and derives the text, the mention
/// rows and the link rows; reads and <c>MessagePosted.v2</c> carry the blocks.
/// </summary>
[Collection(CellCollection.Name)]
public sealed class RichTextTests(PostgresDatabase database, KeycloakRealm keycloak) : MessagingTestBase(database, keycloak)
{
    protected override bool MessageSyncEnabled => true;

    protected override bool RichTextEnabled => true;

    [Fact]
    public async Task Send_WithBlocks_StoresTheCanonicalBlocksTheDerivedTextAndTheLinksAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        const string Sent = """
            [{"type":"paragraph","children":[{"type":"text","text":"Hello MARKER "},{"type":"text","text":"wor","marks":[{"type":"bold"}]},{"type":"text","text":"ld","marks":[{"type":"bold"}]}]},
             {"type":"bulleted_list","children":[{"type":"list_item","children":[{"type":"text","text":"one"}]},{"type":"list_item","children":[{"type":"text","text":"two"}]}]},
             {"type":"paragraph","children":[{"type":"text","text":"docs","marks":[{"type":"italic"},{"type":"link","href":"https://docs.example.test/start"}]},{"type":"text","text":" and "},{"type":"text","text":"again","marks":[{"type":"link","href":"https://docs.example.test/start"}]},{"type":"text","text":" or "},{"type":"text","text":"mail","marks":[{"type":"link","href":"mailto:someone@example.test"}]},{"type":"text","text":" or "},{"type":"text","text":"other","marks":[{"type":"link","href":"http://Other.example.test/"}]}]}]
            """;
        const string Canonical = """
            [{"type":"paragraph","children":[{"type":"text","text":"Hello MARKER "},{"type":"text","text":"world","marks":[{"type":"bold"}]}]},
             {"type":"bulleted_list","children":[{"type":"list_item","children":[{"type":"text","text":"one"}]},{"type":"list_item","children":[{"type":"text","text":"two"}]}]},
             {"type":"paragraph","children":[{"type":"text","text":"docs","marks":[{"type":"italic"},{"type":"link","href":"https://docs.example.test/start"}]},{"type":"text","text":" and "},{"type":"text","text":"again","marks":[{"type":"link","href":"https://docs.example.test/start"}]},{"type":"text","text":" or "},{"type":"text","text":"mail","marks":[{"type":"link","href":"mailto:someone@example.test"}]},{"type":"text","text":" or "},{"type":"text","text":"other","marks":[{"type":"link","href":"http://Other.example.test/"}]}]}]
            """;
        const string Text = "Hello MARKER world\none\ntwo\ndocs and again or mail or other";

        using var response = await SendBlocksAsync(organizationId, channelId, Alice, Sent);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await ReadJsonAsync(response);
        body["text"]!.GetValue<string>().Should().Be(Text);
        ShouldBeSameJson(body["blocks"], Canonical);
        var id = body["id"]!.GetValue<Guid>();

        var row = await QuerySingleAsync("SELECT text, blocks::text FROM messaging.messages WHERE id = $1", id);
        row[0].Should().Be(Text);
        ShouldBeSameJson(JsonNode.Parse((string)row[1]!), Canonical);
        (await QueryAsync("SELECT org_id, conversation_id, position, url, host FROM messaging.message_links WHERE message_id = $1 ORDER BY position", id))
            .Should().BeEquivalentTo(
                new object?[][]
                {
                    [organizationId, channelId, (short)1, "https://docs.example.test/start", "docs.example.test"],
                    [organizationId, channelId, (short)2, "http://Other.example.test/", "other.example.test"],
                },
                options => options.WithStrictOrdering(),
                "each web address once, in order; a mail address is no link row");

        using var read = await GetAsync(MessagesPath(organizationId, channelId), Alice);
        var item = (await ReadJsonAsync(read))["items"]!.AsArray().Should().ContainSingle().Which!;
        item["text"]!.GetValue<string>().Should().Be(Text);
        ShouldBeSameJson(item["blocks"], Canonical);
        Logs.Entries.Should().NotBeEmpty();
        Logs.Entries.Should().NotContain(entry => entry.Contains("MARKER", StringComparison.OrdinalIgnoreCase) || entry.Contains("docs.example", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Send_WithBlocksAndAText_DerivesTheTextFromTheBlocksAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();

        using var response = await PostAsync(
            MessagesPath(organizationId, channelId),
            Alice,
            new { clientMessageId = Guid.NewGuid(), text = "something else", blocks = JsonNode.Parse("""[{"type":"quote","children":[{"type":"text","text":"  from the blocks \n"}]}]""") });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await ReadJsonAsync(response);
        body["text"]!.GetValue<string>().Should().Be("from the blocks", "the derived text is trimmed once, as a whole");
        body["blocks"]![0]!["children"]![0]!["text"]!.GetValue<string>().Should().Be("  from the blocks \n", "a node's text is stored as received");
    }

    [Fact]
    public async Task Send_WithPlainText_AnswersItsOneParagraphAsBlocksAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();

        var sent = await SendMessageAsync(organizationId, channelId, Alice, "plain <b>");

        ShouldBeSameJson(sent["blocks"], """[{"type":"paragraph","children":[{"type":"text","text":"plain <b>"}]}]""");
        using var read = await GetAsync(MessagesPath(organizationId, channelId), Alice);
        ShouldBeSameJson((await ReadJsonAsync(read))["items"]![0]!["blocks"], """[{"type":"paragraph","children":[{"type":"text","text":"plain <b>"}]}]""");
    }

    [Theory]
    [InlineData("""[{"type":"heading","children":[{"type":"text","text":"a"}]}]""", "block.type")]
    [InlineData("""[{"type":"paragraph","children":[{"type":"text","text":"a","marks":[{"type":"link","href":"javascript:alert(1)"}]}]}]""", "link.href")]
    [InlineData("""[{"type":"paragraph","children":[{"type":"text","text":"a","html":"<b>a</b>"}]}]""", "inline.unknown_member")]
    [InlineData("""[{"type":"quote","children":[{"type":"text","text":" \n "}]}]""", "text.empty")]
    [InlineData("""{"type":"paragraph"}""", "blocks")]
    [InlineData("\"<p>html</p>\"", "blocks")]
    public async Task Send_WithBlocksOutsideTheSchema_IsRefusedWithAReasonAndStoresNothingAsync(string blocks, string reason)
    {
        var (organizationId, channelId) = await ChannelAsync();

        using var response = await SendBlocksAsync(organizationId, channelId, Alice, blocks);

        await ShouldBeRefusedAsync(response, "messaging.message_invalid", reason);
        await ShouldHaveStoredNothingAsync(channelId);
    }

    [Fact]
    public async Task Send_WithMoreTextOrMoreBytesThanAllowed_IsRefusedAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        static string Paragraph(int length) => $$"""{"type":"paragraph","children":[{"type":"text","text":"{{new string('a', length)}}"}]}""";

        // The line between two paragraphs counts: 1,999 + 1 + 2,000 characters fit, 2,000 + 1 + 2,000 do not.
        using var longest = await SendBlocksAsync(organizationId, channelId, Alice, $"[{Paragraph(1999)},{Paragraph(2000)}]");
        using var tooLong = await SendBlocksAsync(organizationId, channelId, Alice, $"[{Paragraph(2000)},{Paragraph(2000)}]");
        using var tooLarge = await SendBlocksAsync(organizationId, channelId, Alice, $"[{Paragraph(33_000)}]");

        longest.StatusCode.Should().Be(HttpStatusCode.Created);
        (await ReadJsonAsync(longest))["text"]!.GetValue<string>().Should().HaveLength(4000);
        await ShouldBeRefusedAsync(tooLong, "messaging.message_invalid", "text.length");
        await ShouldBeRefusedAsync(tooLarge, "messaging.message_invalid", "blocks.size");
    }

    [Fact]
    public async Task Send_ARepeatWithBlocks_ReturnsTheStoredMessageAndAddsNothingAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        var clientMessageId = Guid.NewGuid();
        const string Blocks = """[{"type":"paragraph","children":[{"type":"text","text":"link","marks":[{"type":"link","href":"https://example.test/"}]}]}]""";

        using var first = await SendBlocksAsync(organizationId, channelId, Alice, Blocks, clientMessageId);
        using var repeat = await SendBlocksAsync(organizationId, channelId, Alice, Blocks, clientMessageId);

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        repeat.StatusCode.Should().Be(HttpStatusCode.OK);
        var stored = await ReadJsonAsync(repeat);
        stored["id"]!.GetValue<Guid>().Should().Be((await ReadJsonAsync(first))["id"]!.GetValue<Guid>());
        ShouldBeSameJson(stored["blocks"], Blocks);
        (await QuerySingleAsync("SELECT count(*) FROM messaging.message_links WHERE conversation_id = $1", channelId))[0].Should().Be(1L);
        Publisher.MessagesV2.Should().ContainSingle();
    }

    [Fact]
    public async Task Send_MentioningAnActiveMember_PutsTheirNameInTheTextAndStoresOneMentionAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        await AddMemberAsync(organizationId, Bob, SystemRoles.Member);
        var bob = await AccountAsync(Bob);

        using var response = await SendBlocksAsync(organizationId, channelId, Alice, $$"""
            [{"type":"paragraph","children":[{"type":"text","text":"hi "},{"type":"mention","accountId":"{{bob}}"},{"type":"text","text":" and again "},{"type":"mention","accountId":"{{bob}}"}]}]
            """);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await ReadJsonAsync(response);
        body["text"]!.GetValue<string>().Should().Be("hi @Someone and again @Someone");
        body["blocks"]![0]!["children"]![1]!.ToJsonString().Should().Be($$"""{"type":"mention","accountId":"{{bob}}"}""");
        (await QueryAsync("SELECT org_id, conversation_id, message_id, account_id FROM messaging.message_mentions WHERE conversation_id = $1", channelId))
            .Should().BeEquivalentTo(new object?[][] { [organizationId, channelId, body["id"]!.GetValue<Guid>(), bob] });
    }

    [Fact]
    public async Task Send_MentioningSomeoneWhoIsNotAnActiveMember_IsRefusedAndStoresNothingAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        var elsewhere = await ProvisionForAsync(Bob);
        elsewhere.Should().NotBe(organizationId);
        static string Mention(Guid accountId) => $$"""[{"type":"paragraph","children":[{"type":"mention","accountId":"{{accountId}}"}]}]""";

        using var memberOfAnotherOrganization = await SendBlocksAsync(organizationId, channelId, Alice, Mention(await AccountAsync(Bob)));
        using var nobody = await SendBlocksAsync(organizationId, channelId, Alice, Mention(Guid.CreateVersion7()));
        using var everyone = await SendBlocksAsync(organizationId, channelId, Alice, """[{"type":"paragraph","children":[{"type":"mention","special":"everyone"}]}]""");

        await ShouldBeRefusedAsync(memberOfAnotherOrganization, "messaging.mention_invalid", null);
        await ShouldBeRefusedAsync(nobody, "messaging.mention_invalid", null);
        await ShouldBeRefusedAsync(everyone, "messaging.mention_invalid", "group_mentions_unavailable");
        await ShouldHaveStoredNothingAsync(channelId);
    }

    [Fact]
    public async Task Send_MentioningADeactivatedMember_IsRefusedAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        await AddMemberAsync(organizationId, Bob, SystemRoles.Member);
        await ExecuteAsync("UPDATE organization.members SET status = 'deactivated' WHERE org_id = $1 AND account_id = $2", organizationId, await AccountAsync(Bob));

        using var response = await SendBlocksAsync(
            organizationId, channelId, Alice, $$"""[{"type":"paragraph","children":[{"type":"mention","accountId":"{{await AccountAsync(Bob)}}"}]}]""");

        await ShouldBeRefusedAsync(response, "messaging.mention_invalid", null);
    }

    [Fact]
    [Trait("Category", "Privacy")]
    public async Task Send_MentioningAMemberWhoCannotReadTheConversation_StoresNoMentionForThemAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        await AddMemberAsync(organizationId, Bob, SystemRoles.Admin);
        await ExecuteAsync("UPDATE messaging.conversations SET kind = 'private_channel' WHERE id = $1", channelId);
        var alice = await AccountAsync(Alice);
        var bob = await AccountAsync(Bob);

        using var response = await SendBlocksAsync(organizationId, channelId, Alice, $$"""
            [{"type":"paragraph","children":[{"type":"mention","accountId":"{{bob}}"},{"type":"text","text":" "},{"type":"mention","accountId":"{{alice}}"}]}]
            """);

        response.StatusCode.Should().Be(HttpStatusCode.Created, "the author may name any member; the client offers to add them first");
        (await QueryAsync("SELECT account_id FROM messaging.message_mentions WHERE conversation_id = $1", channelId))
            .Should().BeEquivalentTo(new object?[][] { [alice] }, "an administrator who is not in the private channel gets nothing from it");
        using var read = await GetAsync(MessagesPath(organizationId, channelId), Bob);
        read.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    [Trait("Category", "Privacy")]
    public async Task MentionsAndLinks_AcrossOrganizations_AreIsolatedByRowLevelSecurityAsync()
    {
        var (first, firstChannel) = await ChannelAsync();
        var (second, _) = await ChannelAsync();
        using var response = await SendBlocksAsync(first, firstChannel, Alice, $$"""
            [{"type":"paragraph","children":[{"type":"mention","accountId":"{{await AccountAsync(Alice)}}"},{"type":"text","text":"x","marks":[{"type":"link","href":"https://example.test/"}]}]}]
            """);
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        (await CountInOrganizationAsync(first)).Should().Be((1L, 1L));
        (await CountInOrganizationAsync(second)).Should().Be((0L, 0L));
    }

    [Fact]
    public async Task Send_AnnouncesMessagePostedV2WithTheBlocksAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        const string Blocks = """
            [{"type":"numbered_list","children":[{"type":"list_item","children":[{"type":"text","text":"one","marks":[{"type":"strike"},{"type":"code"}]}]}]},
             {"type":"code_block","children":[{"type":"text","text":"x = 1"}]}]
            """;

        using var response = await SendBlocksAsync(organizationId, channelId, Alice, Blocks);
        await SendMessageAsync(organizationId, channelId, Alice, "plain");

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        Publisher.MessagesV2.Should().HaveCount(2);
        Publisher.MessagesV2[0].Message.Text.Should().Be("one\nx = 1");
        ShouldBeSameJson(LiveBlocks(Publisher.MessagesV2[0].Message.Blocks), Blocks);
        ShouldBeSameJson(LiveBlocks(Publisher.MessagesV2[1].Message.Blocks), """[{"type":"paragraph","children":[{"type":"text","text":"plain"}]}]""");
    }

    private static JsonNode? LiveBlocks(IReadOnlyList<object> blocks) =>
        JsonNode.Parse(MessagePackSerializer.ConvertToJson(MessagePackSerializer.Serialize(blocks, cancellationToken: CancellationToken), cancellationToken: CancellationToken));

    private static void ShouldBeSameJson(JsonNode? actual, string expected) =>
        JsonNode.DeepEquals(actual, JsonNode.Parse(expected)).Should().BeTrue($"the blocks should be {expected} but were {actual?.ToJsonString()}");

    private static async Task ShouldBeRefusedAsync(HttpResponseMessage response, string code, string? reason)
    {
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await ReadJsonAsync(response);
        problem["code"]!.GetValue<string>().Should().Be(code);
        (problem["reason"]?.GetValue<string>()).Should().Be(reason);
    }

    private async Task ShouldHaveStoredNothingAsync(Guid channelId)
    {
        (await QuerySingleAsync(
            """
            SELECT (SELECT count(*) FROM messaging.messages WHERE conversation_id = $1)
                 + (SELECT count(*) FROM messaging.message_mentions WHERE conversation_id = $1)
                 + (SELECT count(*) FROM messaging.message_links WHERE conversation_id = $1)
            """,
            channelId))[0].Should().Be(0L);
        Publisher.MessagesV2.Should().BeEmpty();
    }

    private async Task<(long Mentions, long Links)> CountInOrganizationAsync(Guid organizationId)
    {
        (long, long) counts = default;
        await InOrganizationAsync(organizationId, async services =>
        {
            var session = services.GetRequiredService<DatabaseSession>();
            await using var command = new NpgsqlCommand(
                "SELECT (SELECT count(*) FROM messaging.message_mentions), (SELECT count(*) FROM messaging.message_links)", session.Connection, session.Transaction);
            await using var reader = await command.ExecuteReaderAsync(CancellationToken);
            await reader.ReadAsync(CancellationToken);
            counts = (reader.GetInt64(0), reader.GetInt64(1));
        });
        return counts;
    }

    [SuppressMessage("Security", "CA2100", Justification = "The SQL comes from the tests; values are parameters.")]
    private async Task<object?[][]> QueryAsync(string sql, params object[] parameters)
    {
        await using var connection = new NpgsqlConnection(Database.SuperuserConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        }

        await using var reader = await command.ExecuteReaderAsync(CancellationToken);
        var rows = new List<object?[]>();
        while (await reader.ReadAsync(CancellationToken))
        {
            var values = new object?[reader.FieldCount];
            reader.GetValues(values!);
            rows.Add(values);
        }

        return [.. rows];
    }

    private async Task<(Guid OrganizationId, Guid ChannelId)> ChannelAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        return (organizationId, await CreateChannelAsync(organizationId, Alice, "General"));
    }

    private Task<HttpResponseMessage> SendBlocksAsync(Guid organizationId, Guid channelId, string username, string blocks, Guid? clientMessageId = null) =>
        PostAsync(MessagesPath(organizationId, channelId), username, new { clientMessageId = clientMessageId ?? Guid.NewGuid(), blocks = JsonNode.Parse(blocks) });

    private static string MessagesPath(Guid organizationId, Guid channelId) => $"/api/v1/orgs/{organizationId}/conversations/{channelId}/messages";
}
