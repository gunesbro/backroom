using System.Net;
using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Backroom.Modules.Organization.Contracts;

namespace Backroom.Modules.Organization.IntegrationTests;

/// <summary>
/// The invitation email's link (docs/contracts/phase-1.md, flow 3, step 3, and "Invitation security", Code and
/// Transport): derived again from the stored salt and key, only for the registered current version of a pending
/// invitation, with the code in the fragment.
/// </summary>
public sealed partial class InvitationTests
{
    private const string WebOrigin = "https://app.backroom.test";

    [Fact]
    public async Task GetLink_ForTheRegisteredVersion_IsTheInvitePageWithACodeThatHashesToTheStoredHashAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var invitationId = await InviteIdAsync(organizationId, Alice, "kim@example.test");
        (await MarkRegisteredAsync(organizationId, invitationId, codeVersion: 1)).Should().BeTrue();

        var link = (await LinkAsync(organizationId, invitationId, codeVersion: 1))!;

        link.Should().NotBeNull();
        var url = link.Url.AbsoluteUri;
        url.Should().MatchRegex("^https://app\\.backroom\\.test/invite#[A-Za-z0-9_-]{43}$");
        var code = link.Url.Fragment[1..];
        var stored = (byte[])(await QuerySingleAsync("SELECT code_hash FROM organization.invitations WHERE id = $1", invitationId))[0]!;
        SHA256.HashData(Encoding.ASCII.GetBytes(code)).Should().Equal(stored, "the control plane resolves the code by this hash");
        link.Email.Should().Be("kim@example.test");
        link.OrganizationName.Should().Be("Acme");
        link.ToString().Should().NotContain(code).And.NotContain("kim");
    }

    [Fact]
    public async Task GetLink_BeforeTheControlPlaneRegisteredTheVersion_IsNullAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var invitationId = await InviteIdAsync(organizationId, Alice, "kim@example.test");

        (await LinkAsync(organizationId, invitationId, codeVersion: 1)).Should().BeNull("a link must not arrive before the control plane can resolve it");
    }

    [Fact]
    public async Task GetLink_ForAVersionAResendReplaced_IsNullAndTheNewVersionHasAnotherCodeAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var invitationId = await InviteIdAsync(organizationId, Alice, "kim@example.test");
        (await MarkRegisteredAsync(organizationId, invitationId, codeVersion: 1)).Should().BeTrue();
        var first = (await LinkAsync(organizationId, invitationId, codeVersion: 1))!.Url.AbsoluteUri;
        using var resend = await SendAsync(HttpMethod.Post, $"/api/v1/orgs/{organizationId}/invitations/{invitationId}/resend", Alice);
        resend.StatusCode.Should().Be(HttpStatusCode.OK);

        (await LinkAsync(organizationId, invitationId, codeVersion: 1)).Should().BeNull();
        (await LinkAsync(organizationId, invitationId, codeVersion: 2)).Should().BeNull("version 2 is not registered yet");
        (await MarkRegisteredAsync(organizationId, invitationId, codeVersion: 2)).Should().BeTrue();
        (await LinkAsync(organizationId, invitationId, codeVersion: 2))!.Url.AbsoluteUri.Should().NotBe(first, "a resend makes a new code (Uri equality ignores the fragment, so the text is compared)");
    }

    [Fact]
    public async Task GetLink_ForARevokedOrExpiredInvitation_IsNullAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var revoked = await InviteIdAsync(organizationId, Alice, "kim@example.test");
        var expired = await InviteIdAsync(organizationId, Alice, "lee@example.test");
        (await MarkRegisteredAsync(organizationId, revoked, codeVersion: 1)).Should().BeTrue();
        (await MarkRegisteredAsync(organizationId, expired, codeVersion: 1)).Should().BeTrue();
        using var revoke = await SendAsync(HttpMethod.Delete, $"/api/v1/orgs/{organizationId}/invitations/{revoked}", Alice);
        revoke.StatusCode.Should().Be(HttpStatusCode.NoContent);
        await ExpireAsync(expired);

        (await LinkAsync(organizationId, revoked, codeVersion: 1)).Should().BeNull();
        (await LinkAsync(organizationId, expired, codeVersion: 1)).Should().BeNull();
    }

    [Fact]
    public async Task GetLink_ThroughAnotherOrganization_IsNullAsync()
    {
        var first = await ProvisionForAsync(Alice);
        var second = await ProvisionForAsync(Alice);
        var invitationId = await InviteIdAsync(first, Alice, "kim@example.test");
        (await MarkRegisteredAsync(first, invitationId, codeVersion: 1)).Should().BeTrue();

        (await LinkAsync(second, invitationId, codeVersion: 1)).Should().BeNull("row-level security hides the first organization's invitation");
    }

    [Fact]
    public async Task GetLink_WhenTheKeyNoLongerDerivesTheStoredCode_IsNullAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var invitationId = await InviteIdAsync(organizationId, Alice, "kim@example.test");
        (await MarkRegisteredAsync(organizationId, invitationId, codeVersion: 1)).Should().BeTrue();
        await ExecuteAsync("UPDATE organization.invitations SET code_hash = decode(repeat('00', 32), 'hex') WHERE id = $1", invitationId);

        (await LinkAsync(organizationId, invitationId, codeVersion: 1)).Should().BeNull("a link the control plane cannot resolve is never sent");
    }

    [Fact]
    public async Task GetLink_ForAKeyVersionThatIsNoLongerConfigured_IsNullAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var invitationId = await InviteIdAsync(organizationId, Alice, "kim@example.test");
        (await MarkRegisteredAsync(organizationId, invitationId, codeVersion: 1)).Should().BeTrue();
        await ExecuteAsync("UPDATE organization.invitations SET key_version = 7 WHERE id = $1", invitationId);

        (await LinkAsync(organizationId, invitationId, codeVersion: 1)).Should().BeNull();
    }

    [Fact]
    public async Task GetLink_OutsideAnOrganization_ThrowsInsteadOfFindingNothingAsync()
    {
        await using var scope = _cell.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<BuildingBlocks.Application.Persistence.IUnitOfWork>();
        await unitOfWork.BeginAsync(CancellationToken);

        var read = () => scope.ServiceProvider.GetRequiredService<IInvitationLinks>().GetLinkAsync(Guid.CreateVersion7(), 1, CancellationToken);

        await read.Should().ThrowAsync<InvalidOperationException>();
    }

    private async Task<InvitationLink?> LinkAsync(Guid organizationId, Guid invitationId, int codeVersion)
    {
        InvitationLink? link = null;
        await InOrganizationAsync(organizationId, async services =>
            link = await services.GetRequiredService<IInvitationLinks>().GetLinkAsync(invitationId, codeVersion, CancellationToken));
        return link;
    }
}
