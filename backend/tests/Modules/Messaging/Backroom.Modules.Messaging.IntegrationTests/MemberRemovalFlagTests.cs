using System.Net;
using AwesomeAssertions;
using Backroom.Modules.Permissions.Contracts;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;

namespace Backroom.Modules.Messaging.IntegrationTests;

/// <summary>
/// <c>Features:ConversationMemberRemoval</c> off, the default (docs/contracts/phase-2.md, "Live events", "Window after
/// the commit"; owner decision 12): an owner cannot remove other members yet; leaving is unaffected.
/// </summary>
[Collection(CellCollection.Name)]
[Trait("Category", "Privacy")]
public sealed class MemberRemovalFlagTests(PostgresDatabase database, KeycloakRealm keycloak) : MessagingTestBase(database, keycloak)
{
    protected override bool MemberRemovalEnabled => false;

    [Fact]
    public async Task RemoveMember_WhileTheFlagIsOff_IsRefusedAndLeavingStillWorksAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddMemberAsync(organizationId, Bob, SystemRoles.Member);
        var alice = await AccountAsync(Alice);
        var bob = await AccountAsync(Bob);
        var channelId = await CreatePrivateChannelAsync(organizationId, Alice, "Quiet", bob);
        var unknown = Guid.CreateVersion7();

        using var refused = await SendAsync(HttpMethod.Delete, $"/api/v1/orgs/{organizationId}/conversations/{channelId}/members/{bob}", Alice, null);
        // The flag is no oracle: a conversation the caller may not know of is not found, on or off.
        using var missing = await SendAsync(HttpMethod.Delete, $"/api/v1/orgs/{organizationId}/conversations/{unknown}/members/{bob}", Alice, null);
        using var byMember = await SendAsync(HttpMethod.Delete, $"/api/v1/orgs/{organizationId}/conversations/{channelId}/members/{alice}", Bob, null);

        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorCodeAsync(refused)).Should().Be("messaging.member_removal_unavailable");
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ErrorCodeAsync(byMember)).Should().Be("messaging.not_conversation_owner");
        (await QuerySingleAsync("SELECT member_count FROM messaging.conversations WHERE id = $1", channelId)).Should().Equal(2);
        Publisher.Removals.Should().BeEmpty();

        using var left = await SendAsync(HttpMethod.Delete, $"/api/v1/orgs/{organizationId}/conversations/{channelId}/members/{bob}", Bob, null);
        left.StatusCode.Should().Be(HttpStatusCode.NoContent, "removing oneself is leaving");
        Publisher.Removals.Should().Equal([(organizationId, channelId, bob)]);
    }
}
