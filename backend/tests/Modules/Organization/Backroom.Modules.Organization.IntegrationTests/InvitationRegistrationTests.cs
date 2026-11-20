using System.Net;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Backroom.Modules.Organization.Contracts;
using Backroom.Modules.Permissions.Contracts;

namespace Backroom.Modules.Organization.IntegrationTests;

/// <summary>
/// Registering invitations with the control plane (docs/contracts/phase-1.md, flow 3, steps 2 and 3): the connector
/// reads only the current version's hash, and each acknowledged version triggers the email exactly once.
/// </summary>
public sealed partial class InvitationTests
{
    [Fact]
    public async Task GetCodeHash_ForTheCurrentVersion_ReturnsTheStoredHashAsHexAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var invitationId = await InviteIdAsync(organizationId, Alice, "kim@example.test");

        var hash = await CodeHashAsync(organizationId, invitationId, codeVersion: 1);

        var stored = (byte[])(await QuerySingleAsync("SELECT code_hash FROM organization.invitations WHERE id = $1", invitationId))[0]!;
        hash.Should().Be(Convert.ToHexStringLower(stored)).And.MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public async Task GetCodeHash_ForAVersionAResendReplaced_IsNullAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var invitationId = await InviteIdAsync(organizationId, Alice, "kim@example.test");
        using var resend = await SendAsync(HttpMethod.Post, $"/api/v1/orgs/{organizationId}/invitations/{invitationId}/resend", Alice);
        resend.StatusCode.Should().Be(HttpStatusCode.OK);

        (await CodeHashAsync(organizationId, invitationId, codeVersion: 1)).Should().BeNull();
        (await CodeHashAsync(organizationId, invitationId, codeVersion: 2)).Should().NotBeNull();
    }

    [Fact]
    public async Task GetCodeHash_ForARevokedOrExpiredInvitation_IsNullAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var revoked = await InviteIdAsync(organizationId, Alice, "kim@example.test");
        var expired = await InviteIdAsync(organizationId, Alice, "lee@example.test");
        using var revoke = await SendAsync(HttpMethod.Delete, $"/api/v1/orgs/{organizationId}/invitations/{revoked}", Alice);
        await ExpireAsync(expired);

        (await CodeHashAsync(organizationId, revoked, codeVersion: 1)).Should().BeNull();
        (await CodeHashAsync(organizationId, expired, codeVersion: 1)).Should().BeNull();
    }

    [Fact]
    public async Task GetCodeHash_ThroughAnotherOrganization_IsNullAsync()
    {
        var first = await ProvisionForAsync(Alice);
        var second = await ProvisionForAsync(Alice);
        var invitationId = await InviteIdAsync(first, Alice, "kim@example.test");

        (await CodeHashAsync(second, invitationId, codeVersion: 1)).Should().BeNull("row-level security hides the first organization's invitation");
    }

    [Fact]
    public async Task MarkRegistered_ForTheCurrentVersion_PublishesReadyInEnglishNotTheInvitersLanguageAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await ExecuteAsync("UPDATE organization.members SET locale = 'tr-TR' WHERE org_id = $1", organizationId);
        var invitationId = await InviteIdAsync(organizationId, Alice, "kim@example.test");

        var marked = await MarkRegisteredAsync(organizationId, invitationId, codeVersion: 1);

        marked.Should().BeTrue();
        (await QuerySingleAsync("SELECT registered_at IS NOT NULL FROM organization.invitations WHERE id = $1", invitationId))
            .Should().Equal(true);
        var ready = (await OutboxAsync(organizationId)).Where(entry => entry.Type == InvitationReady.EventType).ToList();
        ready.Should().ContainSingle();
        var payload = ready[0].Payload.AsObject();
        payload["invitation_id"]!.GetValue<Guid>().Should().Be(invitationId);
        payload["code_version"]!.GetValue<int>().Should().Be(1);
        payload["locale"]!.GetValue<string>().Should().Be("en-US", "invitation emails are always in English; neither the inviter's nor an account's language decides");
        payload.Select(property => property.Key).Should().BeEquivalentTo(
            ["event_id", "org_id", "occurred_at", "invitation_id", "code_version", "locale"],
            "the event carries neither the code, its hash nor the email");
    }

    [Fact]
    public async Task MarkRegistered_Twice_PublishesReadyOnceAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var invitationId = await InviteIdAsync(organizationId, Alice, "kim@example.test");

        var first = await MarkRegisteredAsync(organizationId, invitationId, codeVersion: 1);
        var second = await MarkRegisteredAsync(organizationId, invitationId, codeVersion: 1);

        first.Should().BeTrue();
        second.Should().BeFalse("a repeated acknowledgement must not send a second email");
        (await OutboxAsync(organizationId)).Count(entry => entry.Type == InvitationReady.EventType).Should().Be(1);
    }

    [Fact]
    public async Task MarkRegistered_ForAVersionAResendReplaced_DoesNothingUntilTheNewVersionAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var invitationId = await InviteIdAsync(organizationId, Alice, "kim@example.test");
        (await MarkRegisteredAsync(organizationId, invitationId, codeVersion: 1)).Should().BeTrue();
        using var resend = await SendAsync(HttpMethod.Post, $"/api/v1/orgs/{organizationId}/invitations/{invitationId}/resend", Alice);
        resend.StatusCode.Should().Be(HttpStatusCode.OK);

        var old = await MarkRegisteredAsync(organizationId, invitationId, codeVersion: 1);
        var current = await MarkRegisteredAsync(organizationId, invitationId, codeVersion: 2);

        old.Should().BeFalse();
        current.Should().BeTrue();
        (await OutboxAsync(organizationId))
            .Where(entry => entry.Type == InvitationReady.EventType)
            .Select(entry => entry.Payload["code_version"]!.GetValue<int>())
            .Should().Equal(1, 2);
    }

    [Fact]
    public async Task MarkRegistered_ForARevokedOrExpiredInvitation_DoesNothingAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var revoked = await InviteIdAsync(organizationId, Alice, "kim@example.test");
        var expired = await InviteIdAsync(organizationId, Alice, "lee@example.test");
        using var revoke = await SendAsync(HttpMethod.Delete, $"/api/v1/orgs/{organizationId}/invitations/{revoked}", Alice);
        await ExpireAsync(expired);

        (await MarkRegisteredAsync(organizationId, revoked, codeVersion: 1)).Should().BeFalse();
        (await MarkRegisteredAsync(organizationId, expired, codeVersion: 1)).Should().BeFalse();
        (await OutboxAsync(organizationId)).Should().NotContain(entry => entry.Type == InvitationReady.EventType);
    }

    [Theory]
    [InlineData("deactivated")]
    [InlineData("removed")]
    public async Task MarkRegistered_ForAnInviterWhoIsNoLongerActive_SendsNothingAsync(string status)
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddBobAsync(organizationId, SystemRoles.Member);
        var invitationId = await InviteIdAsync(organizationId, Bob, "kim@example.test");
        await ExecuteAsync("UPDATE organization.members SET status = $3 WHERE org_id = $1 AND account_id = $2", organizationId, await AccountAsync(Bob), status);

        (await MarkRegisteredAsync(organizationId, invitationId, codeVersion: 1)).Should().BeFalse("acceptance would refuse the invitation");

        (await QuerySingleAsync("SELECT registered_at FROM organization.invitations WHERE id = $1", invitationId)).Should().Equal([null]);
        (await OutboxAsync(organizationId)).Should().NotContain(entry => entry.Type == InvitationReady.EventType);
    }

    [Fact]
    public async Task MarkRegistered_ForAnInviterWhoLostTheRightToInvite_SendsNothingAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddBobAsync(organizationId, SystemRoles.Member);
        var invitationId = await InviteIdAsync(organizationId, Bob, "kim@example.test");
        await TakeInvitingFromMembersAsync(organizationId);

        (await MarkRegisteredAsync(organizationId, invitationId, codeVersion: 1)).Should().BeFalse();

        (await OutboxAsync(organizationId)).Should().NotContain(entry => entry.Type == InvitationReady.EventType);
    }

    [Fact]
    public async Task MarkRegistered_ForAnOldVersionNeverMarked_AfterAResend_DoesNothingAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var invitationId = await InviteIdAsync(organizationId, Alice, "kim@example.test");
        using var resend = await SendAsync(HttpMethod.Post, $"/api/v1/orgs/{organizationId}/invitations/{invitationId}/resend", Alice);
        resend.StatusCode.Should().Be(HttpStatusCode.OK);

        (await MarkRegisteredAsync(organizationId, invitationId, codeVersion: 1)).Should().BeFalse();

        (await QuerySingleAsync("SELECT registered_at FROM organization.invitations WHERE id = $1", invitationId)).Should().Equal([null]);
        (await OutboxAsync(organizationId)).Should().NotContain(entry => entry.Type == InvitationReady.EventType);
    }

    [Fact]
    public async Task GetCodeHash_OutsideAnOrganization_ThrowsInsteadOfFindingNothingAsync()
    {
        await using var scope = _cell.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<BuildingBlocks.Application.Persistence.IUnitOfWork>();
        await unitOfWork.BeginAsync(CancellationToken);

        var read = () => scope.ServiceProvider.GetRequiredService<IInvitationLinks>().GetCodeHashAsync(Guid.CreateVersion7(), 1, CancellationToken);

        await read.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task MarkRegistered_ThroughAnotherOrganization_DoesNothingAsync()
    {
        var first = await ProvisionForAsync(Alice);
        var second = await ProvisionForAsync(Alice);
        var invitationId = await InviteIdAsync(first, Alice, "kim@example.test");

        (await MarkRegisteredAsync(second, invitationId, codeVersion: 1)).Should().BeFalse();

        (await QuerySingleAsync("SELECT registered_at FROM organization.invitations WHERE id = $1", invitationId)).Should().Equal([null]);
        (await OutboxAsync(second)).Should().NotContain(entry => entry.Type == InvitationReady.EventType);
    }

    private async Task<string?> CodeHashAsync(Guid organizationId, Guid invitationId, int codeVersion)
    {
        string? hash = null;
        await InOrganizationAsync(organizationId, async services =>
            hash = await services.GetRequiredService<IInvitationLinks>().GetCodeHashAsync(invitationId, codeVersion, CancellationToken));
        return hash;
    }

    private async Task<bool> MarkRegisteredAsync(Guid organizationId, Guid invitationId, int codeVersion)
    {
        var marked = false;
        await InOrganizationAsync(organizationId, async services =>
            marked = await services.GetRequiredService<IInvitationRegistration>().MarkRegisteredAsync(invitationId, codeVersion, CancellationToken));
        return marked;
    }

    private Task ExpireAsync(Guid invitationId) =>
        ExecuteAsync(
            "UPDATE organization.invitations SET created_at = now() - interval '8 days', expires_at = now() - interval '1 day' WHERE id = $1",
            invitationId);
}
