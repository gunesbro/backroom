using System.Net;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Backroom.Modules.Messaging.Contracts;
using Backroom.Modules.Permissions.Contracts;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;

namespace Backroom.Modules.Messaging.IntegrationTests;

/// <summary>
/// Phase 2 step 9 (docs/contracts/phase-2.md, flow 2 "Edit and delete"): <c>PATCH</c> and <c>DELETE
/// .../messages/{id}</c> with <c>Features:MessageEdit</c> on. An edit is the author's and rewrites the mention and link
/// rows; a delete erases the content and every row derived from it and leaves the tombstone; moderators delete in public
/// channels only; both take the next change number and announce themselves.
/// </summary>
[Collection(CellCollection.Name)]
public sealed class MessageEditDeleteTests(PostgresDatabase database, KeycloakRealm keycloak) : MessagingTestBase(database, keycloak)
{
    private const string Marker = "SECRETMARKER";

    protected override bool RichTextEnabled => true;

    protected override bool MessageEditEnabled => true;

    [Fact]
    public async Task Edit_ByTheAuthor_RewritesTheContentTheVersionTheMentionsAndTheLinksAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        await AddMemberAsync(organizationId, Bob, SystemRoles.Member);
        var bob = await AccountAsync(Bob);
        var carol = await AddSilentMemberAsync(organizationId);
        var sent = await SendBlocksAsync(organizationId, channelId, Alice, $$"""
            [{"type":"paragraph","children":[{"type":"text","text":"{{Marker}} first "},{"type":"mention","accountId":"{{bob}}"},{"type":"text","text":" see ","marks":[{"type":"link","href":"https://old.example.test/a"}]}]}]
            """);
        var messageId = sent["id"]!.GetValue<Guid>();

        using var response = await PatchAsync(organizationId, messageId, Alice, $$"""
            [{"type":"paragraph","children":[{"type":"text","text":"second "},{"type":"mention","accountId":"{{bob}}"},{"type":"mention","accountId":"{{carol}}"},{"type":"text","text":" new","marks":[{"type":"link","href":"https://new.example.test/b"}]}]}]
            """);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadJsonAsync(response);
        body["id"]!.GetValue<Guid>().Should().Be(messageId);
        body["seq"]!.GetValue<long>().Should().Be(sent["seq"]!.GetValue<long>(), "an edit keeps the message's place");
        body["versionSeq"]!.GetValue<long>().Should().Be(2, "the edit took the next change number");
        body["text"]!.GetValue<string>().Should().Be("second @Someone@Silent new");
        body["editedAt"].Should().NotBeNull();
        (await QuerySingleAsync("SELECT change_seq, last_message_seq FROM messaging.conversations WHERE id = $1", channelId)).Should().Equal(2L, 1L);
        (await QuerySingleAsync("SELECT version_seq, seq, edited_at IS NOT NULL FROM messaging.messages WHERE id = $1", messageId)).Should().Equal(2L, 1L, true);

        var mentions = await ColumnAsync("SELECT account_id FROM messaging.message_mentions WHERE message_id = $1", messageId);
        mentions.Should().BeEquivalentTo(new object[] { bob, carol });
        (await ColumnAsync("SELECT url FROM messaging.message_links WHERE message_id = $1", messageId)).Should().Equal("https://new.example.test/b");

        var edited = (await OutboxAsync(organizationId)).Single(entry => entry.Type == MessageEdited.EventType).Payload;
        edited["version_seq"]!.GetValue<long>().Should().Be(2);
        edited["added_mention_account_ids"]!.AsArray().Select(node => node!.GetValue<Guid>()).Should().Equal(carol);
        edited.ToJsonString().Should().NotContain(Marker).And.NotContain("second", "the event carries ids only");

        var live = Publisher.Edited.Should().ContainSingle().Which;
        live.MessageId.Should().Be(messageId);
        live.VersionSeq.Should().Be(2);
        live.ChangeSeq.Should().Be(2);
        live.Text.Should().Be("second @Someone@Silent new");
        live.Epoch.Should().NotBeNullOrEmpty();

        using var changed = await GetAsync($"/api/v1/orgs/{organizationId}/conversations/{channelId}/messages?epoch={body["epoch"]!.GetValue<string>()}&changed_after=1", Alice);
        var item = (await ReadJsonAsync(changed))["items"]!.AsArray().Should().ContainSingle().Which!;
        item["text"]!.GetValue<string>().Should().Be("second @Someone@Silent new");
        item["editedAt"].Should().NotBeNull();
        Logs.Entries.Should().NotContain(entry => entry.Contains(Marker, StringComparison.Ordinal) || entry.Contains("new.example", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Edit_ByAnotherMember_IsForbiddenAndChangesNothingAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        await AddMemberAsync(organizationId, Bob, SystemRoles.Admin);
        var sent = await SendMessageAsync(organizationId, channelId, Alice, "original");

        using var response = await PatchAsync(organizationId, sent["id"]!.GetValue<Guid>(), Bob, Paragraph("hijacked"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorCodeAsync(response)).Should().Be("messaging.not_author");
        (await QuerySingleAsync("SELECT text, version_seq FROM messaging.messages WHERE id = $1", sent["id"]!.GetValue<Guid>())).Should().Equal("original", 1L);
        Publisher.Edited.Should().BeEmpty();
    }

    [Fact]
    public async Task Edit_WithInvalidBlocks_IsRefusedAndTakesNoChangeNumberAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        var sent = await SendMessageAsync(organizationId, channelId, Alice, "original");

        using var unknownType = await PatchAsync(organizationId, sent["id"]!.GetValue<Guid>(), Alice, """[{"type":"heading","children":[]}]""");
        using var empty = await PatchAsync(organizationId, sent["id"]!.GetValue<Guid>(), Alice, """[{"type":"paragraph","children":[{"type":"text","text":"  "}]}]""");
        using var everyone = await PatchAsync(organizationId, sent["id"]!.GetValue<Guid>(), Alice, """[{"type":"paragraph","children":[{"type":"mention","special":"everyone"}]}]""");
        using var missing = await SendAsync(HttpMethod.Patch, $"/api/v1/orgs/{organizationId}/messages/{sent["id"]!.GetValue<Guid>()}", Alice, new { });

        unknownType.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        empty.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        everyone.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        missing.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCodeAsync(unknownType)).Should().Be("messaging.message_invalid");
        (await QuerySingleAsync("SELECT text, version_seq FROM messaging.messages WHERE id = $1", sent["id"]!.GetValue<Guid>())).Should().Equal("original", 1L);
        (await QuerySingleAsync("SELECT change_seq FROM messaging.conversations WHERE id = $1", channelId)).Should().Equal(1L);
    }

    [Fact]
    public async Task Edit_WhenTheMessageIsDeleted_ReturnsMessageDeletedAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        var sent = await SendMessageAsync(organizationId, channelId, Alice, "doomed");
        var messageId = sent["id"]!.GetValue<Guid>();
        using (var deleted = await DeleteMessageAsync(organizationId, messageId, Alice))
        {
            deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        using var response = await PatchAsync(organizationId, messageId, Alice, Paragraph("back from the dead"));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ErrorCodeAsync(response)).Should().Be("messaging.message_deleted");
        (await QuerySingleAsync("SELECT text IS NULL, blocks IS NULL FROM messaging.messages WHERE id = $1", messageId)).Should().Equal(true, true);
    }

    [Fact]
    public async Task Edit_InAnArchivedConversation_IsConflictButDeleteByTheAuthorStillWorksAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        var sent = await SendMessageAsync(organizationId, channelId, Alice, "before the archive");
        var messageId = sent["id"]!.GetValue<Guid>();
        await ExecuteAsync("UPDATE messaging.conversations SET archived_at = now() WHERE id = $1", channelId);

        using var edit = await PatchAsync(organizationId, messageId, Alice, Paragraph("too late"));
        using var delete = await DeleteMessageAsync(organizationId, messageId, Alice);

        edit.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ErrorCodeAsync(edit)).Should().Be("messaging.conversation_archived");
        delete.StatusCode.Should().Be(HttpStatusCode.NoContent, "erasing one's own words is never blocked");
    }

    [Fact]
    [Trait("Category", "Privacy")]
    public async Task Delete_ByTheAuthor_ErasesTheContentTheMentionsAndTheLinksAndLeavesTheTombstoneAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        await AddMemberAsync(organizationId, Bob, SystemRoles.Member);
        var bob = await AccountAsync(Bob);
        var sent = await SendBlocksAsync(organizationId, channelId, Alice, $$"""
            [{"type":"paragraph","children":[{"type":"text","text":"{{Marker}} "},{"type":"mention","accountId":"{{bob}}"},{"type":"text","text":" read","marks":[{"type":"link","href":"https://docs.example.test/x"}]}]}]
            """);
        var messageId = sent["id"]!.GetValue<Guid>();
        (await ColumnAsync("SELECT count(*) FROM messaging.message_mentions WHERE message_id = $1", messageId)).Should().Equal(1L);
        (await ColumnAsync("SELECT count(*) FROM messaging.message_links WHERE message_id = $1", messageId)).Should().Equal(1L);

        using var response = await DeleteMessageAsync(organizationId, messageId, Alice);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await ColumnAsync("SELECT count(*) FROM messaging.message_mentions WHERE message_id = $1", messageId)).Should().Equal([0L], "the mention rows are erased with the message");
        (await ColumnAsync("SELECT count(*) FROM messaging.message_links WHERE message_id = $1", messageId)).Should().Equal([0L], "the link rows are erased with the message");
        (await QuerySingleAsync("SELECT text IS NULL, blocks IS NULL, edited_at IS NULL, deleted_at IS NOT NULL, version_seq, seq FROM messaging.messages WHERE id = $1", messageId))
            .Should().Equal(true, true, true, true, 2L, 1L);
        (await QuerySingleAsync("SELECT change_seq, last_message_seq FROM messaging.conversations WHERE id = $1", channelId)).Should().Equal(2L, 1L);

        // Every read answers the tombstone: its numbers and times, nothing else.
        using var history = await GetAsync($"/api/v1/orgs/{organizationId}/conversations/{channelId}/messages", Bob);
        var historyBody = await ReadJsonAsync(history);
        var tombstone = historyBody["items"]!.AsArray().Should().ContainSingle().Which!.AsObject();
        tombstone["deletedAt"].Should().NotBeNull();
        tombstone["versionSeq"]!.GetValue<long>().Should().Be(2);
        tombstone["text"].Should().BeNull();
        tombstone["blocks"].Should().BeNull();
        tombstone["authorAccountId"].Should().BeNull();
        tombstone["clientMessageId"].Should().BeNull();
        historyBody.ToJsonString().Should().NotContain(Marker);

        var deleted = (await OutboxAsync(organizationId)).Single(entry => entry.Type == MessageDeleted.EventType).Payload;
        deleted["message_id"]!.GetValue<Guid>().Should().Be(messageId);
        deleted["removed_by"].Should().BeNull("the author deleted it");
        deleted.ToJsonString().Should().NotContain(Marker);
        var live = Publisher.Deleted.Should().ContainSingle().Which;
        (live.MessageId, live.VersionSeq, live.ChangeSeq).Should().Be((messageId, 2L, 2L));
        Logs.Entries.Should().NotContain(entry => entry.Contains(Marker, StringComparison.Ordinal) || entry.Contains("docs.example", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Privacy")]
    public async Task Delete_ByAnotherMemberWithoutThePermission_IsForbiddenAndErasesNothingAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        await AddMemberAsync(organizationId, Bob, SystemRoles.Member);
        var sent = await SendMessageAsync(organizationId, channelId, Alice, "stays");

        using var response = await DeleteMessageAsync(organizationId, sent["id"]!.GetValue<Guid>(), Bob);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await QuerySingleAsync("SELECT text, deleted_at IS NULL FROM messaging.messages WHERE id = $1", sent["id"]!.GetValue<Guid>())).Should().Equal("stays", true);
        Publisher.Deleted.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Privacy")]
    public async Task Delete_ByAModeratorInAPublicChannel_ErasesAndNamesTheModeratorInTheEventAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        await AddMemberAsync(organizationId, Bob, SystemRoles.Admin);
        var bob = await AccountAsync(Bob);
        var sent = await SendMessageAsync(organizationId, channelId, Alice, "to be moderated");

        using var response = await DeleteMessageAsync(organizationId, sent["id"]!.GetValue<Guid>(), Bob);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await QuerySingleAsync("SELECT text IS NULL, deleted_at IS NOT NULL FROM messaging.messages WHERE id = $1", sent["id"]!.GetValue<Guid>())).Should().Equal(true, true);
        var deleted = (await OutboxAsync(organizationId)).Single(entry => entry.Type == MessageDeleted.EventType).Payload;
        deleted["removed_by"]!.GetValue<Guid>().Should().Be(bob);
    }

    [Fact]
    [Trait("Category", "Privacy")]
    public async Task Delete_ByAModeratorInAPrivateChannelTheyBelongTo_IsForbiddenAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddMemberAsync(organizationId, Bob, SystemRoles.Admin);
        var privateChannel = await CreatePrivateChannelAsync(organizationId, Alice, "Secret", await AccountAsync(Bob));
        var sent = await SendMessageAsync(organizationId, privateChannel, Alice, "private words");

        using var response = await DeleteMessageAsync(organizationId, sent["id"]!.GetValue<Guid>(), Bob);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, "moderators delete in public channels only");
        (await QuerySingleAsync("SELECT text FROM messaging.messages WHERE id = $1", sent["id"]!.GetValue<Guid>())).Should().Equal("private words");
    }

    [Fact]
    [Trait("Category", "Privacy")]
    public async Task EditAndDelete_ByAnAdministratorWhoCannotReadThePrivateChannel_AnswerLikeAnUnknownMessageAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddMemberAsync(organizationId, Bob, SystemRoles.Admin);
        var privateChannel = await CreatePrivateChannelAsync(organizationId, Alice, "Secret");
        var sent = await SendMessageAsync(organizationId, privateChannel, Alice, "private words");
        var messageId = sent["id"]!.GetValue<Guid>();

        using var edit = await PatchAsync(organizationId, messageId, Bob, Paragraph("peeking"));
        using var delete = await DeleteMessageAsync(organizationId, messageId, Bob);
        using var unknownEdit = await PatchAsync(organizationId, Guid.CreateVersion7(), Bob, Paragraph("peeking"));
        using var unknownDelete = await DeleteMessageAsync(organizationId, Guid.CreateVersion7(), Bob);

        foreach (var response in new[] { edit, delete, unknownEdit, unknownDelete })
        {
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await ErrorCodeAsync(response)).Should().Be("messaging.message_not_found");
        }

        (await QuerySingleAsync("SELECT text, deleted_at IS NULL, version_seq FROM messaging.messages WHERE id = $1", messageId)).Should().Equal("private words", true, 1L);
    }

    [Fact]
    public async Task Delete_ARepeat_ChangesNothingAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        var sent = await SendMessageAsync(organizationId, channelId, Alice, "once");
        var messageId = sent["id"]!.GetValue<Guid>();
        (await DeleteMessageAsync(organizationId, messageId, Alice)).Dispose();

        using var repeat = await DeleteMessageAsync(organizationId, messageId, Alice);

        repeat.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await QuerySingleAsync("SELECT change_seq FROM messaging.conversations WHERE id = $1", channelId)).Should().Equal(2L);
        Publisher.Deleted.Should().ContainSingle();
        (await OutboxAsync(organizationId)).Count(entry => entry.Type == MessageDeleted.EventType).Should().Be(1);
    }

    [Fact]
    public async Task Edit_ThirtyOneTimesAMinute_IsRateLimitedAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        var sent = await SendMessageAsync(organizationId, channelId, Alice, "busy");
        var messageId = sent["id"]!.GetValue<Guid>();

        for (var edit = 0; edit < 30; edit++)
        {
            using var accepted = await PatchAsync(organizationId, messageId, Alice, Paragraph($"edit {edit}"));
            accepted.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using var limited = await PatchAsync(organizationId, messageId, Alice, Paragraph("one too many"));
        using var otherKind = await DeleteMessageAsync(organizationId, messageId, Alice);

        limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await ErrorCodeAsync(limited)).Should().Be("messaging.rate_limited");
        limited.Headers.RetryAfter.Should().NotBeNull();
        otherKind.StatusCode.Should().Be(HttpStatusCode.NoContent, "deletes have a limit of their own");
        (await QuerySingleAsync("SELECT text FROM messaging.messages WHERE id = $1", messageId))[0].Should().Be(DBNull.Value);
    }

    private Task<HttpResponseMessage> PatchAsync(Guid organizationId, Guid messageId, string username, string blocks) =>
        SendAsync(HttpMethod.Patch, $"/api/v1/orgs/{organizationId}/messages/{messageId}", username, new { blocks = JsonNode.Parse(blocks) });

    private Task<HttpResponseMessage> DeleteMessageAsync(Guid organizationId, Guid messageId, string username) =>
        SendAsync(HttpMethod.Delete, $"/api/v1/orgs/{organizationId}/messages/{messageId}", username, null);

    private async Task<JsonNode> SendBlocksAsync(Guid organizationId, Guid channelId, string username, string blocks)
    {
        using var response = await PostAsync(
            $"/api/v1/orgs/{organizationId}/conversations/{channelId}/messages", username, new { clientMessageId = Guid.NewGuid(), blocks = JsonNode.Parse(blocks) });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return await ReadJsonAsync(response);
    }

    private static string Paragraph(string text) => $$"""[{"type":"paragraph","children":[{"type":"text","text":"{{text}}"}]}]""";

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100", Justification = "The SQL comes from the tests; values are parameters.")]
    private async Task<List<object?>> ColumnAsync(string sql, params object[] parameters)
    {
        await using var connection = new Npgsql.NpgsqlConnection(Database.SuperuserConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new Npgsql.NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = parameter });
        }

        await using var reader = await command.ExecuteReaderAsync(CancellationToken);
        var values = new List<object?>();
        while (await reader.ReadAsync(CancellationToken))
        {
            values.Add(reader.GetValue(0));
        }

        return values;
    }

    private async Task<(Guid OrganizationId, Guid ChannelId)> ChannelAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        return (organizationId, await CreateChannelAsync(organizationId, Alice, "General"));
    }
}
