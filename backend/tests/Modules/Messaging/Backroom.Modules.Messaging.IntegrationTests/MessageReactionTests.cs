using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Backroom.Modules.Permissions.Contracts;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;

#pragma warning disable CA5394 // A seeded Random makes the test sequence repeatable; it is not used for security.

namespace Backroom.Modules.Messaging.IntegrationTests;

/// <summary>
/// Phase 2 step 10 (docs/contracts/phase-2.md, flow 2 "Reactions"): <c>PUT</c> and <c>DELETE
/// .../messages/{id}/reactions/{emoji}</c> with <c>Features:MessageReactions</c> on. Both are idempotent; a real change takes the next
/// change number and announces <c>ReactionsChanged</c>; reads carry the summary; the limits hold under concurrency; the privacy rules
/// of reads hold for writes too.
/// </summary>
[Collection(CellCollection.Name)]
public sealed class MessageReactionTests(PostgresDatabase database, KeycloakRealm keycloak) : MessagingTestBase(database, keycloak)
{
    private const string ThumbsUp = "\U0001F44D";
    private const string Heart = "❤️";
    private const string Party = "\U0001F389";

    private static readonly string s_epoch = TestCellEpoch.Initial.ToString(CultureInfo.InvariantCulture);

    protected override bool MessageEditEnabled => true;

    protected override bool MessageReactionsEnabled => true;

    [Fact]
    public async Task Put_NewReaction_StoresItTakesTheNextChangeNumberAndAnnouncesTheSummaryAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        var alice = await AccountAsync(Alice);
        var messageId = (await SendMessageAsync(organizationId, channelId, Alice, "hello"))["id"]!.GetValue<Guid>();

        using var response = await PutAsync(organizationId, messageId, ThumbsUp, Alice);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await QuerySingleAsync("SELECT version_seq, seq FROM messaging.messages WHERE id = $1", messageId)).Should().Equal(2L, 1L);
        (await QuerySingleAsync("SELECT change_seq, last_message_seq FROM messaging.conversations WHERE id = $1", channelId)).Should().Equal(2L, 1L);
        (await QuerySingleAsync("SELECT count(*), min(emoji) FROM messaging.reactions WHERE message_id = $1 AND account_id = $2", messageId, alice))
            .Should().Equal(1L, ThumbsUp);

        var live = Publisher.Reactions.Should().ContainSingle().Which;
        live.OrgId.Should().Be(organizationId);
        live.ConversationId.Should().Be(channelId);
        live.MessageId.Should().Be(messageId);
        live.VersionSeq.Should().Be(2);
        live.ChangeSeq.Should().Be(2);
        live.Epoch.Should().Be(s_epoch);
        var reaction = live.Reactions.Should().ContainSingle().Which;
        (reaction.Emoji, reaction.Count).Should().Be((ThumbsUp, 1));
        reaction.Sample.Should().Equal(alice);

        (await OutboxAsync(organizationId)).Should().NotContain(entry => entry.Type.Contains("reaction", StringComparison.Ordinal), "the contract lists no integration event for reactions");
    }

    [Fact]
    public async Task Put_TheSameReactionAgain_ChangesNothingAndAnnouncesNothingAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        var messageId = (await SendMessageAsync(organizationId, channelId, Alice, "hello"))["id"]!.GetValue<Guid>();
        using (await PutAsync(organizationId, messageId, ThumbsUp, Alice))
        {
        }

        using var again = await PutAsync(organizationId, messageId, ThumbsUp, Alice);

        again.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await QuerySingleAsync("SELECT version_seq FROM messaging.messages WHERE id = $1", messageId)).Should().Equal(2L);
        (await QuerySingleAsync("SELECT change_seq FROM messaging.conversations WHERE id = $1", channelId)).Should().Equal(2L);
        (await QuerySingleAsync("SELECT count(*) FROM messaging.reactions WHERE message_id = $1", messageId)).Should().Equal(1L);
        Publisher.Reactions.Should().HaveCount(1);
    }

    [Fact]
    public async Task Delete_ExistingReaction_RemovesItBumpsTheVersionAndAnnouncesAnEmptySummaryAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        var messageId = (await SendMessageAsync(organizationId, channelId, Alice, "hello"))["id"]!.GetValue<Guid>();
        using (await PutAsync(organizationId, messageId, ThumbsUp, Alice))
        {
        }

        using var response = await RemoveAsync(organizationId, messageId, ThumbsUp, Alice);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await QuerySingleAsync("SELECT version_seq, reaction_summary IS NULL FROM messaging.messages WHERE id = $1", messageId)).Should().Equal(3L, true);
        (await QuerySingleAsync("SELECT count(*) FROM messaging.reactions WHERE message_id = $1", messageId)).Should().Equal(0L);
        Publisher.Reactions.Should().HaveCount(2);
        Publisher.Reactions[1].VersionSeq.Should().Be(3);
        Publisher.Reactions[1].Reactions.Should().BeEmpty();
    }

    [Fact]
    public async Task Delete_ReactionThatIsNotThere_ChangesNothingAndAnnouncesNothingAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        var messageId = (await SendMessageAsync(organizationId, channelId, Alice, "hello"))["id"]!.GetValue<Guid>();

        using var response = await RemoveAsync(organizationId, messageId, ThumbsUp, Alice);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await QuerySingleAsync("SELECT version_seq FROM messaging.messages WHERE id = $1", messageId)).Should().Equal(1L);
        (await QuerySingleAsync("SELECT change_seq FROM messaging.conversations WHERE id = $1", channelId)).Should().Equal(1L);
        Publisher.Reactions.Should().BeEmpty();
    }

    [Fact]
    public async Task Delete_OnlyTheCallersOwnReaction_LeavesTheOthersAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        await AddMemberAsync(organizationId, Bob, SystemRoles.Member);
        await JoinAsync(organizationId, channelId, Bob);
        var messageId = (await SendMessageAsync(organizationId, channelId, Alice, "hello"))["id"]!.GetValue<Guid>();
        using (await PutAsync(organizationId, messageId, ThumbsUp, Alice))
        {
        }

        using (await PutAsync(organizationId, messageId, ThumbsUp, Bob))
        {
        }

        using var response = await RemoveAsync(organizationId, messageId, ThumbsUp, Bob);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var summary = Publisher.Reactions[^1].Reactions.Should().ContainSingle().Which;
        summary.Count.Should().Be(1);
        summary.Sample.Should().Equal(await AccountAsync(Alice));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("ab")]
    [InlineData("1")]
    [InlineData("©")]
    [InlineData("\U0001F44D\U0001F44D")]
    [InlineData("\U0001F44D ")]
    [InlineData("\U0001F44Da")]
    [InlineData("\U0001F468\U0001F3FD‍\U0001F469\U0001F3FD‍\U0001F467\U0001F3FD‍\U0001F466\U0001F3FD")]
    public async Task Put_WithoutExactlyOneShortEmoji_IsRefusedAndStoresNothingAsync(string emoji)
    {
        var (organizationId, channelId) = await ChannelAsync();
        var messageId = (await SendMessageAsync(organizationId, channelId, Alice, "hello"))["id"]!.GetValue<Guid>();

        using var put = await PutAsync(organizationId, messageId, emoji, Alice);
        using var delete = await RemoveAsync(organizationId, messageId, emoji, Alice);

        foreach (var response in new[] { put, delete })
        {
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await ErrorCodeAsync(response)).Should().Be("messaging.reaction_invalid");
        }

        (await QuerySingleAsync("SELECT count(*) FROM messaging.reactions WHERE message_id = $1", messageId)).Should().Equal(0L);
        (await QuerySingleAsync("SELECT change_seq FROM messaging.conversations WHERE id = $1", channelId)).Should().Equal(1L);
    }

    [Fact]
    public async Task Put_WithAnOverlongOrMalformedPathSegment_IsRefusedAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        var messageId = (await SendMessageAsync(organizationId, channelId, Alice, "hello"))["id"]!.GetValue<Guid>();

        foreach (var segment in new[] { new string('a', 5000), "%FF%FE", "%E0%A4%A", "%F0%9F%91%8D%F0%9F%91%8D" })
        {
            using var response = await SendAsync(
                HttpMethod.Put, $"/api/v1/orgs/{organizationId}/messages/{messageId}/reactions/{segment}", Alice, null);

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, segment[..Math.Min(20, segment.Length)]);
            (await ErrorCodeAsync(response)).Should().Be("messaging.reaction_invalid");
        }

        (await QuerySingleAsync("SELECT count(*) FROM messaging.reactions WHERE message_id = $1", messageId)).Should().Equal(0L);
    }

    [Theory]
    [InlineData("\U0001F44D")]
    [InlineData("\U0001F44D\U0001F3FD")]
    [InlineData("❤️")]
    [InlineData("\U0001F1F9\U0001F1F7")]
    [InlineData("1️⃣")]
    [InlineData("\U0001F468‍\U0001F469‍\U0001F467‍\U0001F466")]
    public async Task Put_WithOneEmojiSequence_IsStoredExactlyAsGivenAsync(string emoji)
    {
        var (organizationId, channelId) = await ChannelAsync();
        var messageId = (await SendMessageAsync(organizationId, channelId, Alice, "hello"))["id"]!.GetValue<Guid>();

        // Percent-encoded UTF-8, as every client sends it.
        using var response = await PutAsync(organizationId, messageId, emoji, Alice);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await QuerySingleAsync("SELECT emoji FROM messaging.reactions WHERE message_id = $1", messageId)).Should().Equal(emoji);
    }

    [Fact]
    public async Task Put_WithTheEmojiNotPercentEncoded_IsStoredTooAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        var messageId = (await SendMessageAsync(organizationId, channelId, Alice, "hello"))["id"]!.GetValue<Guid>();

        using var response = await SendAsync(HttpMethod.Put, $"/api/v1/orgs/{organizationId}/messages/{messageId}/reactions/{Party}", Alice, null);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await QuerySingleAsync("SELECT emoji FROM messaging.reactions WHERE message_id = $1", messageId)).Should().Equal(Party);
    }

    [Fact]
    public async Task Put_ByAPersonAtTheirLimit_IsConflictButRepeatsAndRemovalsStillWorkAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        var messageId = (await SendMessageAsync(organizationId, channelId, Alice, "hello"))["id"]!.GetValue<Guid>();
        for (var index = 0; index < 20; index++)
        {
            using var added = await PutAsync(organizationId, messageId, Face(index), Alice);
            added.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        using var twentyFirst = await PutAsync(organizationId, messageId, Face(20), Alice);
        using var repeat = await PutAsync(organizationId, messageId, Face(3), Alice);

        twentyFirst.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ErrorCodeAsync(twentyFirst)).Should().Be("messaging.reaction_limit");
        repeat.StatusCode.Should().Be(HttpStatusCode.NoContent, "a reaction the person already has is not a new one");
        (await QuerySingleAsync("SELECT count(*) FROM messaging.reactions WHERE message_id = $1", messageId)).Should().Equal(20L);
        (await QuerySingleAsync("SELECT change_seq FROM messaging.conversations WHERE id = $1", channelId)).Should().Equal(21L);

        using var removed = await RemoveAsync(organizationId, messageId, Face(0), Alice);
        using var room = await PutAsync(organizationId, messageId, Face(20), Alice);
        removed.StatusCode.Should().Be(HttpStatusCode.NoContent);
        room.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Put_OnAMessageWithFiftyDistinctEmoji_IsConflictForANewOneButNotForAnExistingOneAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        await AddMemberAsync(organizationId, Bob, SystemRoles.Member);
        await JoinAsync(organizationId, channelId, Bob);
        var messageId = (await SendMessageAsync(organizationId, channelId, Alice, "hello"))["id"]!.GetValue<Guid>();
        await SeedAsync(messageId, organizationId, Enumerable.Range(0, 49).Select(index => (Face(index), Guid.CreateVersion7())));

        using var fiftieth = await PutAsync(organizationId, messageId, Face(49), Alice);
        using var fiftyFirst = await PutAsync(organizationId, messageId, Face(50), Bob);
        using var existing = await PutAsync(organizationId, messageId, Face(0), Bob);

        fiftieth.StatusCode.Should().Be(HttpStatusCode.NoContent);
        fiftyFirst.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ErrorCodeAsync(fiftyFirst)).Should().Be("messaging.reaction_limit");
        existing.StatusCode.Should().Be(HttpStatusCode.NoContent, "another person may still add to an emoji the message has");
        (await QuerySingleAsync("SELECT count(DISTINCT emoji), count(*) FROM messaging.reactions WHERE message_id = $1", messageId)).Should().Equal(50L, 51L);
    }

    [Fact]
    public async Task Put_ManyAtOnceByOnePerson_NeverExceedsTheirTwentyAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        var messageId = (await SendMessageAsync(organizationId, channelId, Alice, "hello"))["id"]!.GetValue<Guid>();

        var responses = await Task.WhenAll(Enumerable.Range(0, 28).Select(index => PutAsync(organizationId, messageId, Face(index), Alice)));

        var statuses = responses.Select(response => response.StatusCode).ToList();
        statuses.Count(status => status == HttpStatusCode.NoContent).Should().Be(20);
        statuses.Count(status => status == HttpStatusCode.Conflict).Should().Be(8);
        (await QuerySingleAsync("SELECT count(*) FROM messaging.reactions WHERE message_id = $1", messageId)).Should().Equal(20L);
        (await QuerySingleAsync("SELECT jsonb_array_length(reaction_summary) FROM messaging.messages WHERE id = $1", messageId)).Should().Equal(20);
        (await QuerySingleAsync("SELECT change_seq FROM messaging.conversations WHERE id = $1", channelId)).Should().Equal(21L);
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    [Fact]
    public async Task Put_ByTwoPeopleAtOnceOnTheLastFreeEmojiPlaces_FillsExactlyFiftyAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        await AddMemberAsync(organizationId, Bob, SystemRoles.Member);
        await JoinAsync(organizationId, channelId, Bob);
        var messageId = (await SendMessageAsync(organizationId, channelId, Alice, "hello"))["id"]!.GetValue<Guid>();
        await SeedAsync(messageId, organizationId, Enumerable.Range(0, 45).Select(index => (Face(index), Guid.CreateVersion7())));

        var responses = await Task.WhenAll(
            Enumerable.Range(45, 10).Select(index => PutAsync(organizationId, messageId, Face(index), Alice))
                .Concat(Enumerable.Range(55, 10).Select(index => PutAsync(organizationId, messageId, Face(index), Bob))));

        responses.Count(response => response.StatusCode == HttpStatusCode.NoContent).Should().Be(5);
        responses.Count(response => response.StatusCode == HttpStatusCode.Conflict).Should().Be(15);
        (await QuerySingleAsync("SELECT count(DISTINCT emoji) FROM messaging.reactions WHERE message_id = $1", messageId)).Should().Equal(50L);
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    [Fact]
    public async Task Put_ThirtyOneTimesInTenSeconds_IsRateLimitedAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        var messageId = (await SendMessageAsync(organizationId, channelId, Alice, "hello"))["id"]!.GetValue<Guid>();

        for (var index = 0; index < 30; index++)
        {
            using var allowed = await PutAsync(organizationId, messageId, ThumbsUp, Alice);
            allowed.StatusCode.Should().Be(HttpStatusCode.NoContent, $"request {index + 1}");
        }

        using var limited = await PutAsync(organizationId, messageId, ThumbsUp, Alice);
        using var limitedRemoval = await RemoveAsync(organizationId, messageId, ThumbsUp, Alice);

        limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await ErrorCodeAsync(limited)).Should().Be("messaging.rate_limited");
        limited.Headers.RetryAfter.Should().NotBeNull();
        limitedRemoval.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "adding and removing share one limit");
    }

    [Fact]
    public async Task Put_UnknownMessage_IsNotFoundAsync()
    {
        var (organizationId, _) = await ChannelAsync();

        using var response = await PutAsync(organizationId, Guid.CreateVersion7(), ThumbsUp, Alice);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ErrorCodeAsync(response)).Should().Be("messaging.message_not_found");
    }

    [Fact]
    public async Task PutAndDelete_OnADeletedMessage_AreNotFoundAndLeaveNoReactionAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        var messageId = (await SendMessageAsync(organizationId, channelId, Alice, "doomed"))["id"]!.GetValue<Guid>();
        using (await SendAsync(HttpMethod.Delete, $"/api/v1/orgs/{organizationId}/messages/{messageId}", Alice, null))
        {
        }

        using var put = await PutAsync(organizationId, messageId, ThumbsUp, Alice);
        using var delete = await RemoveAsync(organizationId, messageId, ThumbsUp, Alice);

        foreach (var response in new[] { put, delete })
        {
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await ErrorCodeAsync(response)).Should().Be("messaging.message_not_found");
        }

        (await QuerySingleAsync("SELECT count(*) FROM messaging.reactions WHERE message_id = $1", messageId)).Should().Equal(0L);
    }

    [Fact]
    public async Task PutAndDelete_OnAMessageOfAnotherOrganization_AreNotFoundAsync()
    {
        var (organizationId, _) = await ChannelAsync();
        var otherOrganization = await ProvisionForAsync(Bob);
        var otherChannel = await CreateChannelAsync(otherOrganization, Bob, "Elsewhere");
        var foreignMessage = (await SendMessageAsync(otherOrganization, otherChannel, Bob, "not yours"))["id"]!.GetValue<Guid>();

        using var put = await PutAsync(organizationId, foreignMessage, ThumbsUp, Alice);
        using var delete = await RemoveAsync(organizationId, foreignMessage, ThumbsUp, Alice);

        foreach (var response in new[] { put, delete })
        {
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await ErrorCodeAsync(response)).Should().Be("messaging.message_not_found");
        }

        (await QuerySingleAsync("SELECT count(*) FROM messaging.reactions WHERE message_id = $1", foreignMessage)).Should().Equal(0L);
    }

    [Fact]
    public async Task Put_InAnArchivedConversation_IsConflictAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        var messageId = (await SendMessageAsync(organizationId, channelId, Alice, "hello"))["id"]!.GetValue<Guid>();
        await ExecuteAsync("UPDATE messaging.conversations SET archived_at = now() WHERE id = $1", channelId);

        using var response = await PutAsync(organizationId, messageId, ThumbsUp, Alice);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ErrorCodeAsync(response)).Should().Be("messaging.conversation_archived");
    }

    [Fact]
    public async Task Put_InAPublicChannelTheCallerHasNotJoined_IsForbiddenButTheyCanReadTheSummaryAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        await AddMemberAsync(organizationId, Bob, SystemRoles.Member);
        var messageId = (await SendMessageAsync(organizationId, channelId, Alice, "hello"))["id"]!.GetValue<Guid>();
        using (await PutAsync(organizationId, messageId, ThumbsUp, Alice))
        {
        }

        using var put = await PutAsync(organizationId, messageId, Heart, Bob);
        var page = await ListAsync(organizationId, channelId, Bob);

        put.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorCodeAsync(put)).Should().Be("messaging.not_a_member");
        var reaction = page["items"]![0]!["reactions"]!.AsArray().Should().ContainSingle().Which!;
        (reaction["emoji"]!.GetValue<string>(), reaction["count"]!.GetValue<int>(), reaction["reactedByMe"]!.GetValue<bool>()).Should().Be((ThumbsUp, 1, false));
    }

    [Fact]
    public async Task Put_WhenTheCellHasNoConfirmedEpoch_IsServiceUnavailableAndStoresNothingAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        var messageId = (await SendMessageAsync(organizationId, channelId, Alice, "hello"))["id"]!.GetValue<Guid>();
        CellEpoch.Epoch = null;

        using var response = await PutAsync(organizationId, messageId, ThumbsUp, Alice);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await QuerySingleAsync("SELECT count(*) FROM messaging.reactions WHERE message_id = $1", messageId)).Should().Equal(0L);
    }

    [Fact]
    [Trait("Category", "Privacy")]
    public async Task PutAndDelete_ByANonMemberOfAPrivateChannel_AnswerLikeAnUnknownMessageAndNeverShowTheSummaryAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddMemberAsync(organizationId, Bob, SystemRoles.Admin);
        var privateChannel = await CreatePrivateChannelAsync(organizationId, Alice, "Secret");
        var messageId = (await SendMessageAsync(organizationId, privateChannel, Alice, "private words"))["id"]!.GetValue<Guid>();
        using (await PutAsync(organizationId, messageId, ThumbsUp, Alice))
        {
        }

        using var put = await PutAsync(organizationId, messageId, Heart, Bob);
        using var delete = await RemoveAsync(organizationId, messageId, ThumbsUp, Bob);
        using var unknown = await PutAsync(organizationId, Guid.CreateVersion7(), Heart, Bob);
        using var read = await GetAsync($"/api/v1/orgs/{organizationId}/conversations/{privateChannel}/messages", Bob);
        using var around = await GetAsync($"/api/v1/orgs/{organizationId}/conversations/{privateChannel}/messages?around={messageId}", Bob);

        foreach (var response in new[] { put, delete })
        {
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await ErrorCodeAsync(response)).Should().Be("messaging.message_not_found");
            (await response.Content.ReadAsStringAsync(CancellationToken)).Should().Be(await unknown.Content.ReadAsStringAsync(CancellationToken), "no oracle for the message's existence");
        }

        read.StatusCode.Should().Be(HttpStatusCode.NotFound);
        around.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await read.Content.ReadAsStringAsync(CancellationToken)).Should().NotContain(ThumbsUp);
        (await QuerySingleAsync("SELECT count(*) FROM messaging.reactions WHERE message_id = $1", messageId)).Should().Equal(1L);
        Publisher.Reactions.Should().HaveCount(1);
    }

    [Fact]
    [Trait("Category", "Privacy")]
    public async Task PutAndDelete_ByTheOrganizationOwnerOutsideAPrivateChannel_AnswerLikeAnUnknownMessageAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddMemberAsync(organizationId, Bob, SystemRoles.Member);
        var privateChannel = await CreatePrivateChannelAsync(organizationId, Bob, "Bob's secret");
        var messageId = (await SendMessageAsync(organizationId, privateChannel, Bob, "private words"))["id"]!.GetValue<Guid>();
        using (await PutAsync(organizationId, messageId, ThumbsUp, Bob))
        {
        }

        using var put = await PutAsync(organizationId, messageId, Heart, Alice);
        using var delete = await RemoveAsync(organizationId, messageId, ThumbsUp, Alice);
        using var read = await GetAsync($"/api/v1/orgs/{organizationId}/conversations/{privateChannel}/messages", Alice);

        put.StatusCode.Should().Be(HttpStatusCode.NotFound);
        delete.StatusCode.Should().Be(HttpStatusCode.NotFound);
        read.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ErrorCodeAsync(put)).Should().Be("messaging.message_not_found");
        (await QuerySingleAsync("SELECT count(*) FROM messaging.reactions WHERE message_id = $1", messageId)).Should().Equal(1L);
    }

    [Fact]
    [Trait("Category", "Privacy")]
    public async Task PutAndDelete_ByAMemberWhoLeftAPrivateChannel_AreNotFoundAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddMemberAsync(organizationId, Bob, SystemRoles.Member);
        var privateChannel = await CreatePrivateChannelAsync(organizationId, Alice, "Secret", await AccountAsync(Bob));
        var messageId = (await SendMessageAsync(organizationId, privateChannel, Alice, "private words"))["id"]!.GetValue<Guid>();
        using (await PutAsync(organizationId, messageId, ThumbsUp, Bob))
        {
        }

        using (await SendAsync(HttpMethod.Post, $"/api/v1/orgs/{organizationId}/conversations/{privateChannel}/leave", Bob, null))
        {
        }

        using var put = await PutAsync(organizationId, messageId, Heart, Bob);
        using var delete = await RemoveAsync(organizationId, messageId, ThumbsUp, Bob);

        put.StatusCode.Should().Be(HttpStatusCode.NotFound);
        delete.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await QuerySingleAsync("SELECT count(*) FROM messaging.reactions WHERE message_id = $1", messageId)).Should().Equal(1L);
    }

    [Fact]
    public async Task Read_AMessageWithReactions_CarriesTheSummaryWithCountReactedByMeAndTheEarliestThreeAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        await AddMemberAsync(organizationId, Bob, SystemRoles.Member);
        await JoinAsync(organizationId, channelId, Bob);
        var alice = await AccountAsync(Alice);
        await SendMessageAsync(organizationId, channelId, Alice, "nobody reacts");
        var messageId = (await SendMessageAsync(organizationId, channelId, Alice, "reactions here"))["id"]!.GetValue<Guid>();
        var early = Enumerable.Range(0, 3).Select(_ => Guid.CreateVersion7()).ToArray();
        var start = DateTimeOffset.UtcNow.AddHours(-1);
        await SeedAsync(messageId, organizationId, early.Select((account, index) => (Party, account)), start);
        using (await PutAsync(organizationId, messageId, Party, Bob))
        {
        }

        using (await PutAsync(organizationId, messageId, Heart, Alice))
        {
        }

        using (await PutAsync(organizationId, messageId, ThumbsUp, Alice))
        {
        }

        var forBob = await ListAsync(organizationId, channelId, Bob);
        var forAlice = await ListAsync(organizationId, channelId, Alice);

        var bobItems = forBob["items"]!.AsArray();
        bobItems[0]!["id"]!.GetValue<Guid>().Should().Be(messageId);
        var party = bobItems[0]!["reactions"]![0]!;
        (party["emoji"]!.GetValue<string>(), party["count"]!.GetValue<int>(), party["reactedByMe"]!.GetValue<bool>()).Should().Be((Party, 4, true));
        party["sample"]!.AsArray().Select(node => node!.GetValue<Guid>()).Should().Equal(early, "the three earliest reactors, earliest first");
        var summary = bobItems[0]!["reactions"]!.AsArray().Select(node => (node!["emoji"]!.GetValue<string>(), node["count"]!.GetValue<int>())).ToList();
        summary.Should().Equal([(Party, 4), (Heart, 1), (ThumbsUp, 1)], "in the order of each emoji's first reaction");
        bobItems[1]!["reactions"]!.AsArray().Should().BeEmpty("a message nobody reacted to carries an empty list");

        var aliceReactions = forAlice["items"]![0]!["reactions"]!.AsArray();
        aliceReactions.Select(node => node!["reactedByMe"]!.GetValue<bool>()).Should().Equal(false, true, true);
        aliceReactions[1]!["sample"]!.AsArray().Select(node => node!.GetValue<Guid>()).Should().Equal(alice);
    }

    [Fact]
    public async Task Read_EveryRoute_CarriesTheSameSummaryAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        var messageId = (await SendMessageAsync(organizationId, channelId, Alice, "reactions here"))["id"]!.GetValue<Guid>();
        using (await PutAsync(organizationId, messageId, ThumbsUp, Alice))
        {
        }

        var basePath = $"/api/v1/orgs/{organizationId}/conversations/{channelId}/messages";
        var before = await GetJsonAsync($"{basePath}?before=3", Alice);
        var after = await GetJsonAsync($"{basePath}?after=0", Alice);
        var changed = await GetJsonAsync($"{basePath}?epoch={s_epoch}&changed_after=0", Alice);
        var around = await GetJsonAsync($"{basePath}?around={messageId}", Alice);

        foreach (var page in new[] { before, after, changed, around })
        {
            var reaction = page["items"]!.AsArray().Single()!["reactions"]!.AsArray().Should().ContainSingle().Which!;
            (reaction["emoji"]!.GetValue<string>(), reaction["count"]!.GetValue<int>(), reaction["reactedByMe"]!.GetValue<bool>()).Should().Be((ThumbsUp, 1, true));
        }
    }

    [Fact]
    public async Task Read_AfterAReaction_CatchUpReturnsTheMessageWithItsNewVersionAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        await AddMemberAsync(organizationId, Bob, SystemRoles.Member);
        await JoinAsync(organizationId, channelId, Bob);
        var messageId = (await SendMessageAsync(organizationId, channelId, Alice, "reactions here"))["id"]!.GetValue<Guid>();
        using (await PutAsync(organizationId, messageId, ThumbsUp, Bob))
        {
        }

        var changed = await GetJsonAsync($"/api/v1/orgs/{organizationId}/conversations/{channelId}/messages?epoch={s_epoch}&changed_after=1", Alice);

        var item = changed["items"]!.AsArray().Should().ContainSingle().Which!;
        item["id"]!.GetValue<Guid>().Should().Be(messageId);
        item["versionSeq"]!.GetValue<long>().Should().Be(3, "joining took number 2");
        item["seq"]!.GetValue<long>().Should().Be(2, "the message keeps its place; only its version moved");
        item["reactions"]!.AsArray().Should().ContainSingle();
    }

    [Fact]
    public async Task Read_ReactionsOnAMessageThatIsThenEdited_SurviveTheEditAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        var messageId = (await SendMessageAsync(organizationId, channelId, Alice, "first"))["id"]!.GetValue<Guid>();
        using (await PutAsync(organizationId, messageId, ThumbsUp, Alice))
        {
        }

        using var edit = await SendAsync(
            HttpMethod.Patch, $"/api/v1/orgs/{organizationId}/messages/{messageId}", Alice,
            new { blocks = JsonNode.Parse("""[{"type":"paragraph","children":[{"type":"text","text":"second"}]}]""") });

        edit.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadJsonAsync(edit);
        body["versionSeq"]!.GetValue<long>().Should().Be(3);
        var reaction = body["reactions"]!.AsArray().Should().ContainSingle().Which!;
        (reaction["emoji"]!.GetValue<string>(), reaction["reactedByMe"]!.GetValue<bool>()).Should().Be((ThumbsUp, true));
    }

    [Fact]
    public async Task Send_AnIdempotentRepeat_CarriesTheReactionsOfTheStoredMessageAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        var clientId = Guid.NewGuid();
        var sent = await SendMessageAsync(organizationId, channelId, Alice, "once", clientId);
        sent["reactions"]!.AsArray().Should().BeEmpty();
        using (await PutAsync(organizationId, sent["id"]!.GetValue<Guid>(), ThumbsUp, Alice))
        {
        }

        var repeated = await SendMessageAsync(organizationId, channelId, Alice, "once", clientId);

        repeated["id"]!.GetValue<Guid>().Should().Be(sent["id"]!.GetValue<Guid>());
        repeated["reactions"]!.AsArray().Should().ContainSingle();
    }

    [Fact]
    public async Task Delete_OfAMessageWithReactions_ErasesThemAndTheTombstoneCarriesNoneAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        await AddMemberAsync(organizationId, Bob, SystemRoles.Member);
        await JoinAsync(organizationId, channelId, Bob);
        var messageId = (await SendMessageAsync(organizationId, channelId, Alice, "doomed"))["id"]!.GetValue<Guid>();
        using (await PutAsync(organizationId, messageId, ThumbsUp, Alice))
        {
        }

        using (await PutAsync(organizationId, messageId, Heart, Bob))
        {
        }

        using var deleted = await SendAsync(HttpMethod.Delete, $"/api/v1/orgs/{organizationId}/messages/{messageId}", Alice, null);

        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await QuerySingleAsync("SELECT count(*) FROM messaging.reactions WHERE message_id = $1", messageId)).Should().Equal(0L);
        (await QuerySingleAsync("SELECT reaction_summary IS NULL, deleted_at IS NOT NULL FROM messaging.messages WHERE id = $1", messageId)).Should().Equal(true, true);
        var page = await ListAsync(organizationId, channelId, Bob);
        var tombstone = page["items"]!.AsArray().Single()!.AsObject();
        tombstone.ContainsKey("reactions").Should().BeFalse("a tombstone carries no reactions");
        tombstone["deletedAt"].Should().NotBeNull();
    }

    [Fact]
    public async Task ReactionsChanged_ThroughAFailingPublisher_NeverFailsTheRequestAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        var messageId = (await SendMessageAsync(organizationId, channelId, Alice, "hello"))["id"]!.GetValue<Guid>();
        Publisher.Fail = true;

        using var response = await PutAsync(organizationId, messageId, ThumbsUp, Alice);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await QuerySingleAsync("SELECT count(*) FROM messaging.reactions WHERE message_id = $1", messageId)).Should().Equal(1L);
    }

    [Fact]
    public async Task Put_NeverLogsTheEmojiAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        var messageId = (await SendMessageAsync(organizationId, channelId, Alice, "hello"))["id"]!.GetValue<Guid>();

        using var response = await PutAsync(organizationId, messageId, ThumbsUp, Alice);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        Logs.Entries.Where(entry => entry.StartsWith("Backroom.", StringComparison.Ordinal)).Should().NotContain(entry => entry.Contains(ThumbsUp, StringComparison.Ordinal), "the module logs ids only");
    }

    [Fact]
    public async Task Put_AfterTwoSpellingsOfAHeart_IsOneReactionInTheListedSpellingAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        var messageId = (await SendMessageAsync(organizationId, channelId, Alice, "hello"))["id"]!.GetValue<Guid>();

        using var bare = await PutAsync(organizationId, messageId, "❤", Alice);
        using var qualified = await PutAsync(organizationId, messageId, Heart, Alice);

        bare.StatusCode.Should().Be(HttpStatusCode.NoContent);
        qualified.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await QuerySingleAsync("SELECT count(*), min(emoji) FROM messaging.reactions WHERE message_id = $1", messageId)).Should().Equal(1L, Heart);
        Publisher.Reactions.Should().HaveCount(1, "the second spelling was the same reaction");

        using var removed = await RemoveAsync(organizationId, messageId, "❤", Alice);

        removed.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await QuerySingleAsync("SELECT count(*) FROM messaging.reactions WHERE message_id = $1", messageId)).Should().Equal(0L);
    }

    [Fact]
    public async Task Delete_OwnReactionInAnArchivedConversation_WorksWhileAddingIsConflictAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        var messageId = (await SendMessageAsync(organizationId, channelId, Alice, "hello"))["id"]!.GetValue<Guid>();
        using (await PutAsync(organizationId, messageId, ThumbsUp, Alice))
        {
        }

        await ExecuteAsync("UPDATE messaging.conversations SET archived_at = now() WHERE id = $1", channelId);

        using var add = await PutAsync(organizationId, messageId, Heart, Alice);
        using var remove = await RemoveAsync(organizationId, messageId, ThumbsUp, Alice);

        add.StatusCode.Should().Be(HttpStatusCode.Conflict);
        remove.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await QuerySingleAsync("SELECT count(*) FROM messaging.reactions WHERE message_id = $1", messageId)).Should().Equal(0L);
        Publisher.Reactions[^1].Reactions.Should().BeEmpty();
    }

    [Fact]
    public async Task Delete_OwnReactionAfterLeavingAPublicChannel_WorksWhileAddingIsForbiddenAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        await AddMemberAsync(organizationId, Bob, SystemRoles.Member);
        await JoinAsync(organizationId, channelId, Bob);
        var messageId = (await SendMessageAsync(organizationId, channelId, Alice, "hello"))["id"]!.GetValue<Guid>();
        using (await PutAsync(organizationId, messageId, ThumbsUp, Bob))
        {
        }

        using (await SendAsync(HttpMethod.Post, $"/api/v1/orgs/{organizationId}/conversations/{channelId}/leave", Bob, null))
        {
        }

        using var add = await PutAsync(organizationId, messageId, Heart, Bob);
        using var remove = await RemoveAsync(organizationId, messageId, ThumbsUp, Bob);
        using var removeAgain = await RemoveAsync(organizationId, messageId, ThumbsUp, Bob);

        add.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        remove.StatusCode.Should().Be(HttpStatusCode.NoContent);
        removeAgain.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await QuerySingleAsync("SELECT count(*) FROM messaging.reactions WHERE message_id = $1", messageId)).Should().Equal(0L);
    }

    [Theory]
    [InlineData("dm")]
    [InlineData("group_dm")]
    [Trait("Category", "Privacy")]
    public async Task PutAndDelete_ByANonParticipantOfADirectConversation_AnswerLikeAnUnknownMessageAndNeverShowTheSummaryAsync(string kind)
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddMemberAsync(organizationId, Bob, SystemRoles.Admin);
        var alice = await AccountAsync(Alice);
        var other = await AddSilentMemberAsync(organizationId);
        var conversationId = Guid.CreateVersion7();
        await ExecuteAsync(
            "INSERT INTO messaging.conversations (id, org_id, kind, created_by, created_at, member_count) VALUES ($1, $2, $3, $4, now(), 2)",
            conversationId, organizationId, kind, alice);
        foreach (var member in new[] { alice, other })
        {
            await ExecuteAsync(
                "INSERT INTO messaging.conversation_members (org_id, conversation_id, account_id, role, joined_at, joined_seq, added_by) VALUES ($1, $2, $3, 'member', now(), 0, $4)",
                organizationId, conversationId, member, alice);
        }

        var messageId = (await SendMessageAsync(organizationId, conversationId, Alice, "between us"))["id"]!.GetValue<Guid>();
        using (await PutAsync(organizationId, messageId, ThumbsUp, Alice))
        {
        }

        using var put = await PutAsync(organizationId, messageId, Heart, Bob);
        using var delete = await RemoveAsync(organizationId, messageId, ThumbsUp, Bob);
        using var unknown = await PutAsync(organizationId, Guid.CreateVersion7(), Heart, Bob);
        using var read = await GetAsync($"/api/v1/orgs/{organizationId}/conversations/{conversationId}/messages", Bob);
        using var around = await GetAsync($"/api/v1/orgs/{organizationId}/conversations/{conversationId}/messages?around={messageId}", Bob);

        foreach (var response in new[] { put, delete })
        {
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await ErrorCodeAsync(response)).Should().Be("messaging.message_not_found");
            (await response.Content.ReadAsStringAsync(CancellationToken)).Should().Be(await unknown.Content.ReadAsStringAsync(CancellationToken));
        }

        read.StatusCode.Should().Be(HttpStatusCode.NotFound);
        around.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await read.Content.ReadAsStringAsync(CancellationToken)).Should().NotContain(ThumbsUp);
        (await QuerySingleAsync("SELECT count(*) FROM messaging.reactions WHERE message_id = $1", messageId)).Should().Equal(1L);
        Publisher.Reactions.Should().HaveCount(1);
    }

    [Fact]
    public async Task Summary_AfterARandomSequenceOfAddsAndRemovals_EqualsAFullRebuildFromTheRowsAsync()
    {
        var (organizationId, channelId) = await ChannelAsync();
        await AddMemberAsync(organizationId, Bob, SystemRoles.Member);
        await JoinAsync(organizationId, channelId, Bob);
        var messageId = (await SendMessageAsync(organizationId, channelId, Alice, "reactions here"))["id"]!.GetValue<Guid>();

        // Alice is the earliest reactor with the first emoji; later reactors who never sign in come after her (their times are in the
        // future), so removing her has to read the sample again.
        using (await PutAsync(organizationId, messageId, Face(0), Alice))
        {
        }

        var later = DateTimeOffset.UtcNow.AddHours(1);
        await SeedAsync(messageId, organizationId, Enumerable.Range(0, 5).Select(_ => (Face(0), Guid.CreateVersion7())), later);
        await SeedAsync(messageId, organizationId, Enumerable.Range(0, 2).Select(_ => (Face(1), Guid.CreateVersion7())), later.AddMinutes(1));
        await AssertSummaryEqualsFullRebuildAsync(messageId);
        using (await RemoveAsync(organizationId, messageId, Face(0), Alice))
        {
        }

        await AssertSummaryEqualsFullRebuildAsync(messageId);

        var random = new Random(20261128);
        var present = new HashSet<(string User, int Face)>();
        for (var step = 0; step < 50; step++)
        {
            var user = step % 2 == 0 ? Alice : Bob;
            var face = random.Next(0, 6);
            var remove = present.Contains((user, face)) && random.Next(0, 3) > 0;
            using var response = remove
                ? await RemoveAsync(organizationId, messageId, Face(face), user)
                : await PutAsync(organizationId, messageId, Face(face), user);
            response.StatusCode.Should().Be(HttpStatusCode.NoContent, $"step {step}");
            _ = remove ? present.Remove((user, face)) : present.Add((user, face));
            await AssertSummaryEqualsFullRebuildAsync(messageId);
        }
    }

    private Task<HttpResponseMessage> JoinAsync(Guid organizationId, Guid channelId, string username) =>
        SendAsync(HttpMethod.Post, $"/api/v1/orgs/{organizationId}/conversations/{channelId}/join", username, null);

    private static string Face(int index) => char.ConvertFromUtf32(0x1F600 + index);

    private Task<HttpResponseMessage> PutAsync(Guid organizationId, Guid messageId, string emoji, string username) =>
        SendAsync(HttpMethod.Put, ReactionPath(organizationId, messageId, emoji), username, null);

    private Task<HttpResponseMessage> RemoveAsync(Guid organizationId, Guid messageId, string emoji, string username) =>
        SendAsync(HttpMethod.Delete, ReactionPath(organizationId, messageId, emoji), username, null);

    private static string ReactionPath(Guid organizationId, Guid messageId, string emoji) =>
        $"/api/v1/orgs/{organizationId}/messages/{messageId}/reactions/{Uri.EscapeDataString(emoji)}";

    private async Task<JsonNode> ListAsync(Guid organizationId, Guid channelId, string username) =>
        await GetJsonAsync($"/api/v1/orgs/{organizationId}/conversations/{channelId}/messages", username);

    private async Task<JsonNode> GetJsonAsync(string path, string username)
    {
        using var response = await GetAsync(path, username);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await ReadJsonAsync(response);
    }

    /// <summary>Reactions by people who never sign in, with the summary rebuilt the way the server rebuilds it.</summary>
    private async Task SeedAsync(Guid messageId, Guid organizationId, IEnumerable<(string Emoji, Guid Account)> reactions, DateTimeOffset? start = null)
    {
        var at = start ?? DateTimeOffset.UtcNow.AddHours(-1);
        var index = 0;
        foreach (var (emoji, account) in reactions)
        {
            await ExecuteAsync(
                "INSERT INTO messaging.reactions (org_id, message_id, account_id, emoji, created_at) VALUES ($1, $2, $3, $4, $5)",
                organizationId, messageId, account, emoji, at.AddSeconds(index++));
        }

        await ExecuteAsync(
            """
            UPDATE messaging.messages SET reaction_summary = (
                SELECT jsonb_agg(jsonb_build_object('emoji', r.emoji, 'count', r.total, 'sample', to_jsonb(r.sample), 'at', to_jsonb(r.at)) ORDER BY r.first_at, r.emoji COLLATE "C")
                FROM (SELECT emoji, count(*)::int AS total, min(created_at) AS first_at,
                             (array_agg(account_id ORDER BY created_at, account_id))[1:3] AS sample,
                             (array_agg(to_char(created_at AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US"Z"') ORDER BY created_at, account_id))[1:3] AS at
                      FROM messaging.reactions WHERE message_id = $1 GROUP BY emoji) r)
            WHERE id = $1
            """,
            messageId);
    }

    /// <summary>
    /// The summary the server stores (without the sample times) and the one a full rebuild from all the message's reaction rows gives
    /// (the order of the contract: first reaction time then code point; the earliest three reactors by time then account id).
    /// </summary>
    private async Task AssertSummaryEqualsFullRebuildAsync(Guid messageId)
    {
        var stored = (await QuerySingleAsync("SELECT reaction_summary::text FROM messaging.messages WHERE id = $1", messageId))[0] as string;
        var rebuilt = (await QuerySingleAsync(
            """
            SELECT (SELECT jsonb_agg(jsonb_build_object('emoji', r.emoji, 'count', r.total, 'sample', to_jsonb(r.sample)) ORDER BY r.first_at, r.emoji COLLATE "C")
                    FROM (SELECT emoji, count(*)::int AS total, min(created_at) AS first_at, (array_agg(account_id ORDER BY created_at, account_id))[1:3] AS sample
                          FROM messaging.reactions WHERE message_id = $1 GROUP BY emoji) r)::text
            """,
            messageId))[0] as string;
        var storedNode = stored is null ? null : JsonNode.Parse(stored)!.AsArray();
        if (storedNode is not null)
        {
            foreach (var entry in storedNode)
            {
                entry!.AsObject().Remove("at");
            }
        }

        var storedText = storedNode?.ToJsonString() ?? "null";
        var rebuiltText = rebuilt is null ? "null" : JsonNode.Parse(rebuilt)!.ToJsonString();
        // Same content, whatever the order of an object's members.
        JsonNode.DeepEquals(JsonNode.Parse(storedText), JsonNode.Parse(rebuiltText)).Should().BeTrue($"stored {storedText} must equal rebuilt {rebuiltText}");
    }

    private async Task<(Guid OrganizationId, Guid ChannelId)> ChannelAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        return (organizationId, await CreateChannelAsync(organizationId, Alice, "General"));
    }
}
