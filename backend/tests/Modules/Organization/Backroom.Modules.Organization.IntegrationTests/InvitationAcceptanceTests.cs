using System.Net;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Backroom.BuildingBlocks.Domain.Errors;
using Backroom.Modules.Organization.Contracts;
using Backroom.Modules.Permissions.Contracts;

namespace Backroom.Modules.Organization.IntegrationTests;

/// <summary>
/// The invited person joins (docs/contracts/phase-1.md, flow 3, step 6): the cell checks the invitation's state,
/// expiry, email, the inviter's rights and the membership again, then adds the member with the invited role and
/// publishes <c>organization.member-added.v1</c>, all in the connector's unit of work.
/// </summary>
public sealed partial class InvitationTests
{
    private const string Kim = "kim@example.test";

    [Fact]
    public async Task Accept_APendingInvitation_AddsTheMemberWithTheRoleAndPublishesMemberAddedAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var invitationId = await InviteIdAsync(organizationId, Alice, Kim);
        var accountId = Guid.CreateVersion7();
        var acceptedAt = DateTimeOffset.UtcNow.AddSeconds(-5);

        var result = await AcceptAsync(organizationId, Accepted(invitationId, accountId, " Kim@Example.TEST ", acceptedAt));

        result.IsSuccess.Should().BeTrue();
        (await QuerySingleAsync(
            "SELECT display_name, email, locale, status, version FROM organization.members WHERE org_id = $1 AND account_id = $2",
            organizationId, accountId)).Should().Equal("Kim Test", Kim, "tr-TR", "active", 1L);
        (await RolesOfAsync(organizationId, accountId)).Should().Equal(SystemRoles.Member);
        var invitation = await QuerySingleAsync("SELECT status, accepted_by, accepted_at FROM organization.invitations WHERE id = $1", invitationId);
        invitation[0].Should().Be("accepted");
        invitation[1].Should().Be(accountId);
        new DateTimeOffset((DateTime)invitation[2]!).Should().BeCloseTo(acceptedAt, TimeSpan.FromMilliseconds(1), "the control plane's time is recorded");

        var added = (await OutboxAsync(organizationId)).Where(entry => entry.Type == MemberAdded.EventType).Should().ContainSingle().Subject.Payload.AsObject();
        added.Select(property => property.Key).Should().BeEquivalentTo(
            ["event_id", "org_id", "occurred_at", "account_id", "role", "version"], "the event carries neither the name nor the email");
        added["org_id"]!.GetValue<Guid>().Should().Be(organizationId);
        added["account_id"]!.GetValue<Guid>().Should().Be(accountId);
        added["role"]!.GetValue<string>().Should().Be(SystemRoles.Member);
        added["version"]!.GetValue<long>().Should().Be(1);
    }

    [Fact]
    public async Task Accept_AnInvitationIntoAdmin_AssignsAdminAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var invitationId = await InviteIdAsync(organizationId, Alice, Kim, SystemRoles.Admin);
        var accountId = Guid.CreateVersion7();

        (await AcceptAsync(organizationId, Accepted(invitationId, accountId))).IsSuccess.Should().BeTrue();

        (await RolesOfAsync(organizationId, accountId)).Should().Equal(SystemRoles.Admin);
    }

    [Fact]
    public async Task Accept_Twice_RefusesTheSecondAsNotPendingAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var invitationId = await InviteIdAsync(organizationId, Alice, Kim);

        (await AcceptAsync(organizationId, Accepted(invitationId, Guid.CreateVersion7()))).IsSuccess.Should().BeTrue();
        var second = await AcceptAsync(organizationId, Accepted(invitationId, Guid.CreateVersion7()));

        second.Error!.Code.Should().Be("organization.invitation_not_pending");
        (await CountAsync("SELECT count(*) FROM organization.members WHERE org_id = $1", organizationId)).Should().Be(2, "only the owner and the first account");
    }

    [Fact]
    public async Task Accept_ARevokedInvitation_IsRefusedAsNotPendingAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var invitationId = await InviteIdAsync(organizationId, Alice, Kim);
        using var revoke = await SendAsync(HttpMethod.Delete, $"/api/v1/orgs/{organizationId}/invitations/{invitationId}", Alice);
        revoke.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var accountId = Guid.CreateVersion7();

        var result = await AcceptAsync(organizationId, Accepted(invitationId, accountId));

        result.Error!.Code.Should().Be("organization.invitation_not_pending");
        await ShouldNotBeAMemberAsync(organizationId, accountId);
    }

    [Fact]
    public async Task Accept_AfterTheExpiryAndTheClockTolerance_IsRefusedAsExpiredAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var invitationId = await InviteIdAsync(organizationId, Alice, Kim);
        await ExecuteAsync(
            "UPDATE organization.invitations SET created_at = now() - interval '8 days', expires_at = now() - interval '1 day' WHERE id = $1",
            invitationId);
        var expiresAt = await ExpiresAtAsync(invitationId);
        var accountId = Guid.CreateVersion7();

        var result = await AcceptAsync(organizationId, Accepted(invitationId, accountId, at: expiresAt.AddSeconds(61)));

        result.Error!.Code.Should().Be("organization.invitation_expired");
        await ShouldNotBeAMemberAsync(organizationId, accountId);
        (await QuerySingleAsync("SELECT status FROM organization.invitations WHERE id = $1", invitationId)).Should().Equal("pending");
    }

    [Fact]
    public async Task Accept_DeliveredLateButAcceptedInTime_SucceedsAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var invitationId = await InviteIdAsync(organizationId, Alice, Kim);

        // Delivered after the expiry (the link was down); the control plane's time of acceptance decides.
        await ExecuteAsync(
            "UPDATE organization.invitations SET created_at = now() - interval '8 days', expires_at = now() - interval '1 day' WHERE id = $1",
            invitationId);
        var expiresAt = await ExpiresAtAsync(invitationId);

        var result = await AcceptAsync(organizationId, Accepted(invitationId, Guid.CreateVersion7(), at: expiresAt.AddSeconds(59)));

        result.IsSuccess.Should().BeTrue("acceptance within 60 s of the expiry by the control plane's clock counts");
    }

    [Theory]
    [InlineData("lee@example.test")]
    [InlineData("kim@example.test.evil.test")]
    [InlineData("kim@example.test, lee@example.test")]
    [InlineData("")]
    public async Task Accept_WithAnotherEmail_IsRefusedAsEmailMismatchAsync(string email)
    {
        var organizationId = await ProvisionForAsync(Alice);
        var invitationId = await InviteIdAsync(organizationId, Alice, Kim);
        var accountId = Guid.CreateVersion7();

        var result = await AcceptAsync(organizationId, Accepted(invitationId, accountId, email));

        result.Error!.Code.Should().Be("organization.invitation_email_mismatch");
        await ShouldNotBeAMemberAsync(organizationId, accountId);
    }

    [Theory]
    [InlineData("deactivated")]
    [InlineData("removed")]
    public async Task Accept_WhenTheInviterIsNoLongerActive_IsRefusedAsInviterNotAuthorizedAsync(string status)
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddBobAsync(organizationId, SystemRoles.Member);
        var invitationId = await InviteIdAsync(organizationId, Bob, Kim);
        await ExecuteAsync("UPDATE organization.members SET status = $3 WHERE org_id = $1 AND account_id = $2", organizationId, await AccountAsync(Bob), status);
        var accountId = Guid.CreateVersion7();

        var result = await AcceptAsync(organizationId, Accepted(invitationId, accountId));

        result.Error!.Code.Should().Be("organization.inviter_not_authorized");
        await ShouldNotBeAMemberAsync(organizationId, accountId);
    }

    [Fact]
    public async Task Accept_WhenTheInviterLostTheRightToInvite_IsRefusedAsInviterNotAuthorizedAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddBobAsync(organizationId, SystemRoles.Member);
        var invitationId = await InviteIdAsync(organizationId, Bob, Kim);
        await TakeInvitingFromMembersAsync(organizationId);

        var result = await AcceptAsync(organizationId, Accepted(invitationId, Guid.CreateVersion7()));

        result.Error!.Code.Should().Be("organization.inviter_not_authorized");
    }

    [Fact]
    public async Task Accept_WhenTheInviterCanNoLongerGrantTheRole_IsRefusedAsInviterNotAuthorizedAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddBobAsync(organizationId, SystemRoles.Admin);
        var invitationId = await InviteIdAsync(organizationId, Bob, Kim, SystemRoles.Admin);

        // Bob is demoted: Member cannot grant Admin.
        await ExecuteAsync(
            """
            UPDATE permissions.member_roles SET role_id = (SELECT id FROM permissions.roles WHERE org_id = $1 AND key = 'member')
            WHERE org_id = $1 AND account_id = $2
            """,
            organizationId, await AccountAsync(Bob));

        var result = await AcceptAsync(organizationId, Accepted(invitationId, Guid.CreateVersion7()));

        result.Error!.Code.Should().Be("organization.inviter_not_authorized");
    }

    [Fact]
    public async Task Accept_ByADeactivatedMember_IsRefusedAsMemberDeactivatedAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var invitationId = await InviteIdAsync(organizationId, Alice, Kim);
        var accountId = Guid.CreateVersion7();
        await AddMemberAsync(organizationId, accountId, "kim.old@example.test", "deactivated");

        var result = await AcceptAsync(organizationId, Accepted(invitationId, accountId));

        result.Error!.Code.Should().Be("organization.member_deactivated");
        (await QuerySingleAsync("SELECT status, version FROM organization.members WHERE org_id = $1 AND account_id = $2", organizationId, accountId))
            .Should().Equal("deactivated", 1L);
        (await OutboxAsync(organizationId)).Should().NotContain(entry => entry.Type == MemberAdded.EventType);
    }

    [Fact]
    public async Task Accept_ByAnActiveMember_MarksTheInvitationAcceptedAndChangesNothingElseAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddBobAsync(organizationId, SystemRoles.Member);
        var invitationId = await InviteIdAsync(organizationId, Bob, Kim);
        var accountId = Guid.CreateVersion7();
        await AddMemberAsync(organizationId, accountId, "kim.old@example.test", "active");

        // The inviter's rights no longer matter: nothing is granted.
        await TakeInvitingFromMembersAsync(organizationId);
        var result = await AcceptAsync(organizationId, Accepted(invitationId, accountId));

        result.IsSuccess.Should().BeTrue();
        (await QuerySingleAsync("SELECT status, accepted_by FROM organization.invitations WHERE id = $1", invitationId)).Should().Equal("accepted", accountId);
        (await QuerySingleAsync(
            "SELECT display_name, email, status, version FROM organization.members WHERE org_id = $1 AND account_id = $2",
            organizationId, accountId)).Should().Equal("Someone", "kim.old@example.test", "active", 1L);
        (await RolesOfAsync(organizationId, accountId)).Should().BeEmpty("no role is assigned to an existing member");
        (await OutboxAsync(organizationId)).Should().NotContain(entry => entry.Type == MemberAdded.EventType);
    }

    [Fact]
    public async Task Accept_ByARemovedMember_MakesThemActiveAgainWithOnlyTheInvitedRoleAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var invitationId = await InviteIdAsync(organizationId, Alice, Kim);
        var accountId = Guid.CreateVersion7();
        await AddMemberAsync(organizationId, accountId, "kim.old@example.test", "removed");

        // Roles a removal left behind must not come back with the new membership.
        await InOrganizationAsync(organizationId, async services =>
        {
            var provisioning = services.GetRequiredService<IPermissionsProvisioning>();
            await provisioning.AssignRoleAsync(organizationId, accountId, SystemRoles.Admin, CancellationToken);
            await provisioning.AssignRoleAsync(organizationId, accountId, SystemRoles.Owner, CancellationToken);
        });

        var result = await AcceptAsync(organizationId, Accepted(invitationId, accountId));

        result.IsSuccess.Should().BeTrue();
        (await QuerySingleAsync(
            "SELECT display_name, email, locale, status, version FROM organization.members WHERE org_id = $1 AND account_id = $2",
            organizationId, accountId)).Should().Equal("Kim Test", Kim, "tr-TR", "active", 2L);
        (await RolesOfAsync(organizationId, accountId)).Should().Equal([SystemRoles.Member], "the stale Admin and Owner roles are gone");
        (await RolesOfAsync(organizationId, await AccountAsync(Alice))).Should().Equal(SystemRoles.Owner);
        (await OutboxAsync(organizationId)).Where(entry => entry.Type == MemberAdded.EventType)
            .Should().ContainSingle().Which.Payload["version"]!.GetValue<long>().Should().Be(2);
    }

    [Fact]
    public async Task Accept_WithAnAcceptanceTimeInTheFuture_IsRefusedAsInvalidAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var invitationId = await InviteIdAsync(organizationId, Alice, Kim);
        var accountId = Guid.CreateVersion7();

        var future = await AcceptAsync(organizationId, Accepted(invitationId, accountId, at: DateTimeOffset.UtcNow.AddMinutes(5)));
        var withinTolerance = await AcceptAsync(organizationId, Accepted(invitationId, accountId, at: DateTimeOffset.UtcNow.AddSeconds(30)));

        future.Error!.Code.Should().Be("organization.invitation_acceptance_invalid", "a future time would stretch the expiry check");
        withinTolerance.IsSuccess.Should().BeTrue("the clocks may differ by up to 60 s");
    }

    [Fact]
    public async Task Accept_AnUnknownInvitation_IsNotFoundSoTheCallerRetriesAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);

        var result = await AcceptAsync(organizationId, Accepted(Guid.CreateVersion7(), Guid.CreateVersion7()));

        result.Error!.Kind.Should().Be(ErrorKind.NotFound);
        result.Error.Code.Should().Be("organization.invitation_not_found");
    }

    [Fact]
    public async Task Accept_InAnOrganizationTheCellDoesNotHave_IsNotFoundWithoutFailingAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var invitationId = await InviteIdAsync(organizationId, Alice, Kim);

        var result = await AcceptAsync(Guid.CreateVersion7(), Accepted(invitationId, Guid.CreateVersion7()));

        result.Error!.Kind.Should().Be(ErrorKind.NotFound, "the organization may arrive later; the lock is not even tried");
    }

    [Fact]
    public async Task Accept_ThroughAnotherOrganization_FindsNothingAndChangesNothingAsync()
    {
        var first = await ProvisionForAsync(Alice);
        var second = await ProvisionForAsync(Alice);
        var invitationId = await InviteIdAsync(first, Alice, Kim);
        var accountId = Guid.CreateVersion7();

        var result = await AcceptAsync(second, Accepted(invitationId, accountId));

        result.Error!.Code.Should().Be("organization.invitation_not_found", "row-level security hides the first organization's invitation");
        (await QuerySingleAsync("SELECT status FROM organization.invitations WHERE id = $1", invitationId)).Should().Equal("pending");
        (await CountAsync("SELECT count(*) FROM organization.members WHERE account_id = $1", accountId)).Should().Be(0);
        (await OutboxAsync(second)).Should().NotContain(entry => entry.Type == MemberAdded.EventType);
    }

    public static TheoryData<string> InvalidAcceptances => new() { "empty account", "empty name", "long name", "NUL in name", "short locale", "long locale" };

    [Theory]
    [MemberData(nameof(InvalidAcceptances))]
    public async Task Accept_WithValuesTheColumnsCannotHold_IsRefusedAsInvalidAsync(string kind)
    {
        var organizationId = await ProvisionForAsync(Alice);
        var invitationId = await InviteIdAsync(organizationId, Alice, Kim);
        var accepted = Accepted(invitationId, Guid.CreateVersion7());
        accepted = kind switch
        {
            "empty account" => accepted with { AccountId = Guid.Empty },
            "empty name" => accepted with { DisplayName = "" },
            "long name" => accepted with { DisplayName = new string('a', 401) },
            "NUL in name" => accepted with { DisplayName = "Ki\0m" },
            "short locale" => accepted with { Locale = "e" },
            _ => accepted with { Locale = new string('a', 36) },
        };

        var result = await AcceptAsync(organizationId, accepted);

        result.Error!.Code.Should().Be("organization.invitation_acceptance_invalid");
        (await QuerySingleAsync("SELECT status FROM organization.invitations WHERE id = $1", invitationId)).Should().Equal("pending");
    }

    private static AcceptedInvitation Accepted(Guid invitationId, Guid accountId, string email = Kim, DateTimeOffset? at = null) =>
        new(invitationId, accountId, "Kim Test", email, "tr-TR", at ?? DateTimeOffset.UtcNow);

    private async Task<Result> AcceptAsync(Guid organizationId, AcceptedInvitation invitation)
    {
        Result result = default;
        await InOrganizationAsync(organizationId, async services =>
            result = await services.GetRequiredService<IInvitationAcceptance>().AcceptAsync(invitation, CancellationToken));
        return result;
    }

    private async Task<Guid> InviteIdAsync(Guid organizationId, string username, string email, string role)
    {
        using var response = await InviteAsync(organizationId, username, email, role);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await ReadJsonAsync(response))["id"]!.GetValue<Guid>();
    }

    private async Task<DateTimeOffset> ExpiresAtAsync(Guid invitationId) =>
        new((DateTime)(await QuerySingleAsync("SELECT expires_at FROM organization.invitations WHERE id = $1", invitationId))[0]!);

    private async Task<long> CountAsync(string sql, Guid value) => (long)(await QuerySingleAsync(sql, value))[0]!;

    private async Task<List<string>> RolesOfAsync(Guid organizationId, Guid accountId)
    {
        var count = await QuerySingleAsync(
            "SELECT count(*), string_agg(r.key, ',') FROM permissions.member_roles m JOIN permissions.roles r ON r.org_id = m.org_id AND r.id = m.role_id WHERE m.org_id = $1 AND m.account_id = $2",
            organizationId, accountId);
        return count[1] is string keys ? [.. keys.Split(',')] : [];
    }

    private async Task ShouldNotBeAMemberAsync(Guid organizationId, Guid accountId)
    {
        (await QuerySingleAsync("SELECT count(*) FROM organization.members WHERE org_id = $1 AND account_id = $2", organizationId, accountId))
            .Should().Equal(0L);
        (await RolesOfAsync(organizationId, accountId)).Should().BeEmpty();
        (await OutboxAsync(organizationId)).Should().NotContain(entry => entry.Type == MemberAdded.EventType);
    }
}
