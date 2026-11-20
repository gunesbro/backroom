using System.Net;
using System.Net.Http.Headers;
using AwesomeAssertions;
using Backroom.Modules.Organization.Contracts;
using Backroom.Modules.Permissions.Contracts;

namespace Backroom.Modules.Organization.IntegrationTests;

/// <summary>
/// Revoking and resending invitations (docs/contracts/phase-1.md, flow 3, step 8): only the inviter or a manager
/// can, to anyone else the invitation does not exist, and a resend replaces the code and counts as a send.
/// </summary>
public sealed partial class InvitationTests
{
    [Fact]
    public async Task Revoke_AsTheInviter_RevokesItAndPublishesTheEventAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var invitationId = await InviteIdAsync(organizationId, Alice, "kim@example.test");

        using var response = await SendAsync(HttpMethod.Delete, $"/api/v1/orgs/{organizationId}/invitations/{invitationId}", Alice);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await QuerySingleAsync("SELECT status, revoked_by FROM organization.invitations WHERE id = $1", invitationId))
            .Should().Equal("revoked", await AccountAsync(Alice));
        var revoked = (await OutboxAsync(organizationId)).Where(entry => entry.Type == InvitationRevoked.EventType).ToList();
        revoked.Should().ContainSingle();
        revoked[0].Payload["invitation_id"]!.GetValue<Guid>().Should().Be(invitationId);
        using var list = await GetAsync($"/api/v1/orgs/{organizationId}/invitations", Alice);
        (await ReadJsonAsync(list))["invitations"]!.AsArray().Should().BeEmpty();
    }

    [Fact]
    public async Task Revoke_AsAManager_RevokesSomeoneElsesAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddBobAsync(organizationId, SystemRoles.Member);
        var invitationId = await InviteIdAsync(organizationId, Bob, "kim@example.test");

        using var response = await SendAsync(HttpMethod.Delete, $"/api/v1/orgs/{organizationId}/invitations/{invitationId}", Alice);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Revoke_SomeoneElsesAsAMember_AnswersLikeAnUnknownInvitationAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddBobAsync(organizationId, SystemRoles.Member);
        var invitationId = await InviteIdAsync(organizationId, Alice, "kim@example.test");

        using var someoneElses = await SendAsync(HttpMethod.Delete, $"/api/v1/orgs/{organizationId}/invitations/{invitationId}", Bob);
        using var unknown = await SendAsync(HttpMethod.Delete, $"/api/v1/orgs/{organizationId}/invitations/{Guid.CreateVersion7()}", Bob);

        someoneElses.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await ReadJsonAsync(someoneElses);
        body["code"]!.GetValue<string>().Should().Be("organization.invitation_not_found");
        body["detail"]!.GetValue<string>().Should().Be((await ReadJsonAsync(unknown))["detail"]!.GetValue<string>());
        (await QuerySingleAsync("SELECT status FROM organization.invitations WHERE id = $1", invitationId)).Should().Equal("pending");
    }

    [Fact]
    public async Task Revoke_ThroughAnotherOrganization_IsNotFoundAsync()
    {
        var first = await ProvisionForAsync(Alice);
        var second = await ProvisionForAsync(Alice);
        var invitationId = await InviteIdAsync(first, Alice, "kim@example.test");

        using var response = await SendAsync(HttpMethod.Delete, $"/api/v1/orgs/{second}/invitations/{invitationId}", Alice);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "row-level security hides the first organization's invitation");
        (await QuerySingleAsync("SELECT status FROM organization.invitations WHERE id = $1", invitationId)).Should().Equal("pending");
    }

    [Fact]
    public async Task Revoke_Twice_IsNotPendingTheSecondTimeAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var invitationId = await InviteIdAsync(organizationId, Alice, "kim@example.test");
        using var first = await SendAsync(HttpMethod.Delete, $"/api/v1/orgs/{organizationId}/invitations/{invitationId}", Alice);

        using var second = await SendAsync(HttpMethod.Delete, $"/api/v1/orgs/{organizationId}/invitations/{invitationId}", Alice);

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadJsonAsync(second))["code"]!.GetValue<string>().Should().Be("organization.invitation_not_pending");
        (await OutboxAsync(organizationId)).Count(entry => entry.Type == InvitationRevoked.EventType).Should().Be(1);
    }

    [Fact]
    public async Task Revoke_ByAnInviterWhoLostTheRightToInvite_StillWorksAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddBobAsync(organizationId, SystemRoles.Member);
        var invitationId = await InviteIdAsync(organizationId, Bob, "kim@example.test");
        await TakeInvitingFromMembersAsync(organizationId);

        using var response = await SendAsync(HttpMethod.Delete, $"/api/v1/orgs/{organizationId}/invitations/{invitationId}", Bob);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Resend_AsTheInviter_ReplacesTheCodeAndKeepsTheExpiryAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var invitationId = await InviteIdAsync(organizationId, Alice, "kim@example.test");
        const string Row = "SELECT salt, code_hash, code_version, expires_at, registered_at IS NULL, status FROM organization.invitations WHERE id = $1";
        var before = await QuerySingleAsync(Row, invitationId);

        using var response = await SendAsync(HttpMethod.Post, $"/api/v1/orgs/{organizationId}/invitations/{invitationId}/resend", Alice);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await ReadJsonAsync(response)).AsObject();
        body["id"]!.GetValue<Guid>().Should().Be(invitationId);
        body.Select(property => property.Key).Should().BeEquivalentTo(["id", "email", "role", "createdBy", "createdAt", "expiresAt"]);
        var after = await QuerySingleAsync(Row, invitationId);
        ((byte[])after[0]!).Should().NotEqual((byte[])before[0]!, "a resend has a new salt");
        ((byte[])after[1]!).Should().NotEqual((byte[])before[1]!, "so the old link stops working");
        after[2].Should().Be(2);
        after[3].Should().Be(before[3], "the original expiry stays");
        after[4].Should().Be(true, "the control plane has to register the new code");
        after[5].Should().Be("pending");

        var created = (await OutboxAsync(organizationId)).Where(entry => entry.Type == InvitationCreated.EventType).ToList();
        created.Should().HaveCount(2);
        created[1].Payload["invitation_id"]!.GetValue<Guid>().Should().Be(invitationId);
        created[1].Payload["code_version"]!.GetValue<int>().Should().Be(2);
    }

    [Fact]
    public async Task Resend_SomeoneElsesAsAMember_IsNotFoundAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddBobAsync(organizationId, SystemRoles.Member);
        var invitationId = await InviteIdAsync(organizationId, Alice, "kim@example.test");

        using var response = await SendAsync(HttpMethod.Post, $"/api/v1/orgs/{organizationId}/invitations/{invitationId}/resend", Bob);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await QuerySingleAsync("SELECT code_version FROM organization.invitations WHERE id = $1", invitationId)).Should().Equal(1);
    }

    [Fact]
    public async Task Resend_ARevokedInvitation_IsNotPendingAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var invitationId = await InviteIdAsync(organizationId, Alice, "kim@example.test");
        using var revoked = await SendAsync(HttpMethod.Delete, $"/api/v1/orgs/{organizationId}/invitations/{invitationId}", Alice);

        using var response = await SendAsync(HttpMethod.Post, $"/api/v1/orgs/{organizationId}/invitations/{invitationId}/resend", Alice);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadJsonAsync(response))["code"]!.GetValue<string>().Should().Be("organization.invitation_not_pending");
    }

    [Fact]
    public async Task Resend_AnExpiredInvitation_IsRefusedAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var invitationId = await InviteIdAsync(organizationId, Alice, "kim@example.test");
        await ExecuteAsync("UPDATE organization.invitations SET expires_at = now() - interval '1 minute' WHERE id = $1", invitationId);

        using var response = await SendAsync(HttpMethod.Post, $"/api/v1/orgs/{organizationId}/invitations/{invitationId}/resend", Alice);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadJsonAsync(response))["code"]!.GetValue<string>().Should().Be("organization.invitation_expired");
    }

    [Fact]
    public async Task Resend_ByAnInviterWhoLostTheRightToInvite_IsForbiddenAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddBobAsync(organizationId, SystemRoles.Member);
        var invitationId = await InviteIdAsync(organizationId, Bob, "kim@example.test");
        // Only the right to invite goes: Bob can still grant Member, so only that check can refuse him.
        await TakeInvitingFromMembersAsync(organizationId);

        using var response = await SendAsync(HttpMethod.Post, $"/api/v1/orgs/{organizationId}/invitations/{invitationId}/resend", Bob);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Resend_AnAdminInvitation_AsAMemberWhoSentIt_IsForbiddenAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddBobAsync(organizationId, SystemRoles.Member);
        var invitationId = await InviteIdAsync(organizationId, Bob, "kim@example.test");
        // As if Bob could grant Admin when he invited, and cannot any more.
        await ExecuteAsync("UPDATE organization.invitations SET role = 'admin' WHERE id = $1", invitationId);

        using var response = await SendAsync(HttpMethod.Post, $"/api/v1/orgs/{organizationId}/invitations/{invitationId}/resend", Bob);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Resend_CountsTowardsTheLimitForTheRecipientAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var invitationId = await InviteIdAsync(organizationId, Alice, "kim@example.test");
        var resend = $"/api/v1/orgs/{organizationId}/invitations/{invitationId}/resend";
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var response = await SendAsync(HttpMethod.Post, resend, Alice);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using var thirdResend = await SendAsync(HttpMethod.Post, resend, Alice);
        using var newInvitation = await InviteAsync(organizationId, Alice, "kim@example.test");

        await ShouldBeRateLimitedAsync(thirdResend, TimeSpan.FromHours(23), TimeSpan.FromHours(24));
        await ShouldBeRateLimitedAsync(newInvitation, TimeSpan.FromHours(23), TimeSpan.FromHours(24));
        (await QuerySingleAsync("SELECT code_version FROM organization.invitations WHERE id = $1", invitationId)).Should().Equal(3);
    }

    [Fact]
    public async Task Resend_CountsTowardsTheLimitForTheSenderAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var invitationId = await InviteIdAsync(organizationId, Alice, "kim@example.test");
        await SeedInvitationsAsync(organizationId, await AccountAsync(Alice), 49, "revoked", TimeSpan.FromHours(2));

        using var response = await SendAsync(HttpMethod.Post, $"/api/v1/orgs/{organizationId}/invitations/{invitationId}/resend", Alice);

        await ShouldBeRateLimitedAsync(response, TimeSpan.FromHours(21), TimeSpan.FromHours(23));
    }

    [Fact]
    public async Task Revoke_SomeoneElsesRevokedInvitationAsAMember_IsNotFoundRatherThanNotPendingAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddBobAsync(organizationId, SystemRoles.Member);
        var invitationId = await InviteIdAsync(organizationId, Alice, "kim@example.test");
        using var revoked = await SendAsync(HttpMethod.Delete, $"/api/v1/orgs/{organizationId}/invitations/{invitationId}", Alice);

        using var response = await SendAsync(HttpMethod.Delete, $"/api/v1/orgs/{organizationId}/invitations/{invitationId}", Bob);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Resend_ThroughAnotherOrganization_IsNotFoundAsync()
    {
        var first = await ProvisionForAsync(Alice);
        var second = await ProvisionForAsync(Alice);
        var invitationId = await InviteIdAsync(first, Alice, "kim@example.test");

        using var response = await SendAsync(HttpMethod.Post, $"/api/v1/orgs/{second}/invitations/{invitationId}/resend", Alice);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await QuerySingleAsync("SELECT code_version FROM organization.invitations WHERE id = $1", invitationId)).Should().Equal(1);
    }

    [Fact]
    public async Task Resend_AsAManager_WhenTheInviterCanNoLongerInvite_IsRefusedAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddBobAsync(organizationId, SystemRoles.Member);
        var invitationId = await InviteIdAsync(organizationId, Bob, "kim@example.test");
        await TakeInvitingFromMembersAsync(organizationId);

        using var response = await SendAsync(HttpMethod.Post, $"/api/v1/orgs/{organizationId}/invitations/{invitationId}/resend", Alice);

        // Acceptance would refuse it, so a new email would only carry a link that cannot work.
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadJsonAsync(response))["code"]!.GetValue<string>().Should().Be("organization.inviter_not_authorized");
    }

    [Fact]
    public async Task Resend_AsAManager_WhenTheInviterLeft_IsRefusedAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddBobAsync(organizationId, SystemRoles.Member);
        var invitationId = await InviteIdAsync(organizationId, Bob, "kim@example.test");
        await ExecuteAsync("UPDATE organization.members SET status = 'removed' WHERE org_id = $1 AND account_id = $2", organizationId, await AccountAsync(Bob));

        using var response = await SendAsync(HttpMethod.Post, $"/api/v1/orgs/{organizationId}/invitations/{invitationId}/resend", Alice);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadJsonAsync(response))["code"]!.GetValue<string>().Should().Be("organization.inviter_not_authorized");
    }

    [Fact]
    public async Task Resend_AddsToTheSendersDailyCountAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await SeedInvitationsAsync(organizationId, await AccountAsync(Alice), 48, "revoked", TimeSpan.FromHours(2));
        var invitationId = await InviteIdAsync(organizationId, Alice, "kim@example.test");
        using var resent = await SendAsync(HttpMethod.Post, $"/api/v1/orgs/{organizationId}/invitations/{invitationId}/resend", Alice);
        resent.StatusCode.Should().Be(HttpStatusCode.OK, "it is the 50th send");

        using var response = await InviteAsync(organizationId, Alice, "lee@example.test");

        await ShouldBeRateLimitedAsync(response, TimeSpan.FromHours(21), TimeSpan.FromHours(23));
    }

    [Fact]
    public async Task Resend_AddsToTheOrganizationsDailyCountAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await SeedInvitationsAsync(organizationId, Guid.CreateVersion7(), 198, "revoked", TimeSpan.FromHours(2));
        var invitationId = await InviteIdAsync(organizationId, Alice, "kim@example.test");
        using var resent = await SendAsync(HttpMethod.Post, $"/api/v1/orgs/{organizationId}/invitations/{invitationId}/resend", Alice);
        resent.StatusCode.Should().Be(HttpStatusCode.OK, "it is the organization's 200th send");

        using var response = await InviteAsync(organizationId, Alice, "lee@example.test");

        await ShouldBeRateLimitedAsync(response, TimeSpan.FromHours(21), TimeSpan.FromHours(23));
    }

    [Fact]
    public async Task Resend_AsAManager_CountsForTheManagerNotTheInviterAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddBobAsync(organizationId, SystemRoles.Member);
        var bob = await AccountAsync(Bob);
        var invitationId = await InviteIdAsync(organizationId, Bob, "kim@example.test");
        await SeedInvitationsAsync(organizationId, bob, 49, "revoked", TimeSpan.FromHours(2));

        using var response = await SendAsync(HttpMethod.Post, $"/api/v1/orgs/{organizationId}/invitations/{invitationId}/resend", Alice);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "Bob has used his 50 sends, Alice has not");
        (await QuerySingleAsync("SELECT resent_by FROM organization.invitation_resends WHERE invitation_id = $1", invitationId))
            .Should().Equal(await AccountAsync(Alice));
    }

    [Fact]
    public async Task Resend_TwiceAtOnce_GivesEachItsOwnVersionAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var invitationId = await InviteIdAsync(organizationId, Alice, "kim@example.test");
        var token = await TokenAsync(Alice);
        var path = $"/api/v1/orgs/{organizationId}/invitations/{invitationId}/resend";

        var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return await _client.SendAsync(request, CancellationToken);
        }));

        try
        {
            responses.Should().OnlyContain(response => response.StatusCode == HttpStatusCode.OK);
            (await QuerySingleAsync("SELECT code_version FROM organization.invitations WHERE id = $1", invitationId)).Should().Equal(3);
            (await QuerySingleAsync("SELECT count(*) FROM organization.invitation_resends WHERE invitation_id = $1", invitationId)).Should().Equal(2L);
            (await OutboxAsync(organizationId)).Where(entry => entry.Type == InvitationCreated.EventType)
                .Select(entry => entry.Payload["code_version"]!.GetValue<int>()).Should().BeEquivalentTo([1, 2, 3]);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    private async Task<Guid> InviteIdAsync(Guid organizationId, string username, string email)
    {
        using var response = await InviteAsync(organizationId, username, email);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await ReadJsonAsync(response))["id"]!.GetValue<Guid>();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string username)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(username));
        return await _client.SendAsync(request, CancellationToken);
    }

    // The Member role of the organization loses members.invite-by-email.
    private Task TakeInvitingFromMembersAsync(Guid organizationId) =>
        ExecuteAsync(
            """
            DELETE FROM permissions.role_permissions
            WHERE org_id = $1 AND permission = 'members.invite-by-email'
              AND role_id IN (SELECT id FROM permissions.roles WHERE org_id = $1 AND key = 'member')
            """,
            organizationId);
}
