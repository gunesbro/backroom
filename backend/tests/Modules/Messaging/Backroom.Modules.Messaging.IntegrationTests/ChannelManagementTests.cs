using System.Net;
using AwesomeAssertions;
using Backroom.Modules.Messaging.Contracts;
using Backroom.Modules.Permissions.Contracts;
using Backroom.Modules.Realtime.Contracts;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;

namespace Backroom.Modules.Messaging.IntegrationTests;

/// <summary>
/// Managing public channels (docs/contracts/phase-2.md, flow 1): rename, description, archive and unarchive need
/// <c>channels.manage-public</c>; names stay unique among open public channels; membership changes are announced to
/// the person's own account group.
/// </summary>
[Collection(CellCollection.Name)]
public sealed class ChannelManagementTests(PostgresDatabase database, KeycloakRealm keycloak) : MessagingTestBase(database, keycloak)
{
    [Fact]
    public async Task Update_APublicChannel_NeedsManagePublicAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddMemberAsync(organizationId, Bob, SystemRoles.Member);
        var channelId = await CreateChannelAsync(organizationId, Bob, "General");
        var path = $"/api/v1/orgs/{organizationId}/conversations/{channelId}";

        // Bob created the channel, but a public channel has no owner: the permission decides.
        using var refused = await SendAsync(HttpMethod.Patch, path, Bob, new { name = "Renamed" });
        using var refusedArchive = await SendAsync(HttpMethod.Post, $"{path}/archive", Bob, null);
        using var response = await SendAsync(HttpMethod.Patch, path, Alice, new { name = "Renamed", description = "Everything" });

        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorCodeAsync(refused)).Should().Be("permissions.forbidden");
        refusedArchive.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadJsonAsync(response);
        body["name"]!.GetValue<string>().Should().Be("Renamed");
        body["description"]!.GetValue<string>().Should().Be("Everything");
        body["kind"]!.GetValue<string>().Should().Be("public_channel");
        (await OutboxAsync(organizationId)).Should().ContainSingle(entry => entry.Type == ConversationChanged.EventType)
            .Which.Payload["change"]!.GetValue<string>().Should().Be("renamed");
        Publisher.Conversations.Should().ContainSingle().Which.Name.Should().Be("Renamed");

        // A description alone is no rename; an empty one removes it.
        using var cleared = await SendAsync(HttpMethod.Patch, path, Alice, new { description = "" });
        cleared.StatusCode.Should().Be(HttpStatusCode.OK);
        (await QuerySingleAsync("SELECT name, description IS NULL, change_seq FROM messaging.conversations WHERE id = $1", channelId)).Should().Equal("Renamed", true, 2L);
        (await OutboxAsync(organizationId)).Count(entry => entry.Type == ConversationChanged.EventType).Should().Be(1);
    }

    [Fact]
    public async Task Update_WithBadInput_IsRefusedAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(organizationId, Alice, "General");
        var path = $"/api/v1/orgs/{organizationId}/conversations/{channelId}";

        using var nothing = await SendAsync(HttpMethod.Patch, path, Alice, new { });
        using var badName = await SendAsync(HttpMethod.Patch, path, Alice, new { name = " " });
        using var longDescription = await SendAsync(HttpMethod.Patch, path, Alice, new { description = new string('d', 251) });
        using var controlCharacter = await SendAsync(HttpMethod.Patch, path, Alice, new { description = "a\nb" });
        using var fits = await SendAsync(HttpMethod.Patch, path, Alice, new { description = new string('d', 250) });

        (await ErrorCodeAsync(nothing)).Should().Be("messaging.conversation_update_invalid");
        (await ErrorCodeAsync(badName)).Should().Be("messaging.channel_name_invalid");
        (await ErrorCodeAsync(longDescription)).Should().Be("messaging.channel_description_invalid");
        (await ErrorCodeAsync(controlCharacter)).Should().Be("messaging.channel_description_invalid");
        fits.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Rename_APublicChannel_ToAnOpenPublicName_IsConflictAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await CreateChannelAsync(organizationId, Alice, "General");
        var channelId = await CreateChannelAsync(organizationId, Alice, "Random");
        var path = $"/api/v1/orgs/{organizationId}/conversations/{channelId}";

        using var taken = await SendAsync(HttpMethod.Patch, path, Alice, new { name = "GENERAL" });
        using var ownNameInAnotherCase = await SendAsync(HttpMethod.Patch, path, Alice, new { name = "RANDOM" });

        taken.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ErrorCodeAsync(taken)).Should().Be("messaging.channel_name_taken");
        ownNameInAnotherCase.StatusCode.Should().Be(HttpStatusCode.OK);
        (await QuerySingleAsync("SELECT name FROM messaging.conversations WHERE id = $1", channelId)).Should().Equal("RANDOM");
    }

    [Fact]
    public async Task Archive_APublicChannel_FreesItsNameAndUnarchiveNeedsItBackAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(organizationId, Alice, "General");
        var path = $"/api/v1/orgs/{organizationId}/conversations/{channelId}";

        using var archived = await SendAsync(HttpMethod.Post, $"{path}/archive", Alice, null);
        var replacement = await CreateChannelAsync(organizationId, Alice, "General");
        using var blocked = await SendAsync(HttpMethod.Post, $"{path}/unarchive", Alice, null);
        (await SendAsync(HttpMethod.Post, $"/api/v1/orgs/{organizationId}/conversations/{replacement}/archive", Alice, null)).EnsureSuccessStatusCode();
        using var reopened = await SendAsync(HttpMethod.Post, $"{path}/unarchive", Alice, null);
        using var reopenedAgain = await SendAsync(HttpMethod.Post, $"{path}/unarchive", Alice, null);

        archived.StatusCode.Should().Be(HttpStatusCode.NoContent);
        blocked.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ErrorCodeAsync(blocked)).Should().Be("messaging.channel_name_taken");
        reopened.StatusCode.Should().Be(HttpStatusCode.NoContent);
        reopenedAgain.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await QuerySingleAsync("SELECT archived_at IS NULL FROM messaging.conversations WHERE id = $1", channelId)).Should().Equal(true);
        (await OutboxAsync(organizationId)).Where(entry => entry.Type == ConversationChanged.EventType && entry.Payload["conversation_id"]!.GetValue<Guid>() == channelId)
            .Select(entry => entry.Payload["change"]!.GetValue<string>()).Should().Equal(["archived", "unarchived"]);
    }

    [Fact]
    public async Task Change_WhileTheEpochIsUnconfirmed_IsUnavailableAndStoresNothingAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(organizationId, Alice, "General");
        CellEpoch.Epoch = null;

        using var rename = await SendAsync(HttpMethod.Patch, $"/api/v1/orgs/{organizationId}/conversations/{channelId}", Alice, new { name = "Renamed" });
        using var archive = await SendAsync(HttpMethod.Post, $"/api/v1/orgs/{organizationId}/conversations/{channelId}/archive", Alice, null);

        rename.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await ErrorCodeAsync(rename)).Should().Be("cell.epoch_unconfirmed");
        archive.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await QuerySingleAsync("SELECT name, archived_at IS NULL, change_seq FROM messaging.conversations WHERE id = $1", channelId)).Should().Equal("General", true, 0L);
    }

    [Fact]
    public async Task JoinAndLeave_TellThePersonsOwnAccountGroupAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddMemberAsync(organizationId, Bob, null);
        var bob = await AccountAsync(Bob);
        var channelId = await CreateChannelAsync(organizationId, Alice, "General");

        (await SendAsync(HttpMethod.Post, $"/api/v1/orgs/{organizationId}/conversations/{channelId}/join", Bob, null)).EnsureSuccessStatusCode();
        (await SendAsync(HttpMethod.Post, $"/api/v1/orgs/{organizationId}/conversations/{channelId}/leave", Bob, null)).EnsureSuccessStatusCode();

        Publisher.Memberships.Where(payload => payload.AccountId == bob).Should().Equal(
            new MembershipChangedPayload(organizationId, bob, channelId, MembershipChangedPayload.Added),
            new MembershipChangedPayload(organizationId, bob, channelId, MembershipChangedPayload.Removed));
    }

    [Fact]
    public async Task CreatePublicChannel_WithInitialMembers_AddsThemAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var silent = await AddSilentMemberAsync(organizationId);

        using var response = await PostAsync($"/api/v1/orgs/{organizationId}/channels", Alice, new { name = "Launch", memberAccountIds = new[] { silent } });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var channelId = (await ReadJsonAsync(response))["id"]!.GetValue<Guid>();
        (await QuerySingleAsync("SELECT kind, member_count, owner_account_id IS NULL FROM messaging.conversations WHERE id = $1", channelId))
            .Should().Equal("public_channel", 2, true);
        Publisher.Channels.Should().ContainSingle();
    }

    [Fact]
    public async Task JoinAndLeave_TakeAChangeNumberAndTellTheConversationTheMemberCountAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddMemberAsync(organizationId, Bob, null);
        var channelId = await CreateChannelAsync(organizationId, Alice, "General");
        var epoch = TestCellEpoch.Initial.ToString(System.Globalization.CultureInfo.InvariantCulture);

        (await SendAsync(HttpMethod.Post, $"/api/v1/orgs/{organizationId}/conversations/{channelId}/join", Bob, null)).EnsureSuccessStatusCode();
        (await SendAsync(HttpMethod.Post, $"/api/v1/orgs/{organizationId}/conversations/{channelId}/leave", Bob, null)).EnsureSuccessStatusCode();

        Publisher.Conversations.Should().Equal(
            new ConversationUpdatedPayload(organizationId, channelId, "General", null, null, 2, 1, epoch),
            new ConversationUpdatedPayload(organizationId, channelId, "General", null, null, 1, 2, epoch));
        (await QuerySingleAsync("SELECT change_seq, last_message_seq, member_count FROM messaging.conversations WHERE id = $1", channelId)).Should().Equal(2L, 0L, 1);
    }

    [Fact]
    public async Task Leave_WhileTheEpochIsUnconfirmed_StillLeavesWithoutALiveUpdateAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddMemberAsync(organizationId, Bob, null);
        var bob = await AccountAsync(Bob);
        var channelId = await CreateChannelAsync(organizationId, Alice, "General");
        (await SendAsync(HttpMethod.Post, $"/api/v1/orgs/{organizationId}/conversations/{channelId}/join", Bob, null)).EnsureSuccessStatusCode();
        CellEpoch.Epoch = null;

        using var left = await SendAsync(HttpMethod.Post, $"/api/v1/orgs/{organizationId}/conversations/{channelId}/leave", Bob, null);

        left.StatusCode.Should().Be(HttpStatusCode.NoContent, "leaving is never held back by the control plane link");
        Publisher.Removals.Should().Equal([(organizationId, channelId, bob)]);
        Publisher.Conversations.Should().ContainSingle("only the join, which had an epoch, was announced");
        (await QuerySingleAsync("SELECT change_seq, member_count FROM messaging.conversations WHERE id = $1", channelId)).Should().Equal(2L, 1);
    }
}
