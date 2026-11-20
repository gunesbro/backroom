using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Backroom.BuildingBlocks.Application.Accounts;
using Backroom.BuildingBlocks.Application.Persistence;
using Backroom.ControlPlane.Contracts.V1;
using Backroom.Modules.CellManagement.Contracts;
using Backroom.Modules.Directory.Application.Invitations;
using Backroom.Modules.Directory.Presentation;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;

namespace Backroom.Modules.Directory.IntegrationTests;

/// <summary>
/// Accepting an invitation at the control plane and the cell's answer (docs/contracts/phase-1.md, flow 3, steps 5
/// and 7, "States" and "Invitation security"): only the invited, verified address can accept; a repeat answers the
/// current state; every unusable code gets the same answer; the cell's answer settles it.
/// </summary>
[Trait("Category", "Privacy")]
[Collection(DirectoryCollection.Name)]
public sealed class InvitationAcceptTests(PostgresDatabase database, KeycloakRealm keycloak) : IAsyncLifetime
{
    private readonly string _invitee = $"invitee-{Guid.CreateVersion7():N}@example.test";
    private const string Bob = "bob@example.test";

    private readonly byte[] _emailHmacKey = RandomNumberGenerator.GetBytes(32);
    private readonly string _cellId = "a" + Guid.CreateVersion7().ToString("N")[^10..];
    private WebApplication _controlPlane = null!;
    private HttpClient _client = null!;
    private Guid _inviteeId;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _controlPlane = await DirectoryUnderTest.StartAsync(database, keycloak, CancellationToken, emailHmacKey: Convert.ToBase64String(_emailHmacKey));
        _client = _controlPlane.GetTestClient();
        // An account of its own per test: the latest acceptance and the memberships of an account are read whole.
        await keycloak.CreateUserAsync(_invitee);
        _inviteeId = Guid.Parse(await keycloak.UserIdAsync(_invitee));
        await ExecuteAsync(
            "INSERT INTO cell_management.cells (id, name, type, region, public_url, status, created_at, last_heartbeat_at) VALUES ($1, 'Test', 'shared', 'eu', $2, 'registered', now(), now())",
            _cellId, $"https://{_cellId}.example.test/");
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _controlPlane.DisposeAsync();
    }

    [Fact]
    public async Task Accept_ForAPendingInvitationToTheAccount_StartsAcceptingAndSendsItToTheCellAsync()
    {
        var invitation = await AddInvitationAsync(_invitee);

        using var response = await AcceptAsync(_invitee, invitation.Code);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        response.Headers.Location!.OriginalString.Should().Be("/api/v1/invitations/acceptance");
        var body = (await ReadJsonAsync(response)).AsObject();
        body.Select(property => property.Key).Should().BeEquivalentTo(["organizationId", "status"], "neither the code nor an address is sent back");
        body["organizationId"]!.GetValue<Guid>().Should().Be(invitation.OrganizationId);
        body["status"]!.GetValue<string>().Should().Be("accepting");

        (await ScalarAsync<string>("SELECT status FROM directory.invitations WHERE invitation_id = $1", invitation.Id)).Should().Be("accepting");
        (await ScalarAsync<Guid>("SELECT accepting_account_id FROM directory.invitations WHERE invitation_id = $1", invitation.Id)).Should().Be(_inviteeId);
        var acceptedAt = await ScalarAsync<DateTime>("SELECT accepted_at FROM directory.invitations WHERE invitation_id = $1", invitation.Id);
        (await MembershipAsync(invitation.OrganizationId, _inviteeId)).Should().Be("pending");

        var message = (await SentAsync()).Should().ContainSingle().Subject;
        message.Should().BeEquivalentTo(new InvitationAccepted
        {
            OrganizationId = invitation.OrganizationId.ToString("D"),
            InvitationId = invitation.Id.ToString("D"),
            AccountId = _inviteeId.ToString("D"),
            DisplayName = message.DisplayName,
            Email = _invitee,
            Locale = "en-US",
            AcceptedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(DateTime.SpecifyKind(acceptedAt, DateTimeKind.Utc)),
        });
        message.DisplayName.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Accept_RepeatedByTheSameAccount_AnswersTheCurrentStateAndSendsOnceAsync()
    {
        var invitation = await AddInvitationAsync(_invitee);
        var token = await TokenAsync(_invitee);
        using var first = await AcceptWithTokenAsync(_client, token, invitation.Code);

        using var again = await AcceptWithTokenAsync(_client, token, invitation.Code);
        await ExecuteAsync("UPDATE directory.invitations SET status = 'used', used_at = now() WHERE invitation_id = $1", invitation.Id);
        using var afterUse = await AcceptWithTokenAsync(_client, token, invitation.Code);

        first.StatusCode.Should().Be(HttpStatusCode.Accepted);
        again.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await ReadJsonAsync(again))["status"]!.GetValue<string>().Should().Be("accepting");
        afterUse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await ReadJsonAsync(afterUse))["status"]!.GetValue<string>().Should().Be("used");
        (await SentAsync()).Should().ContainSingle("a repeat sends nothing again");
    }

    [Fact]
    public async Task Accept_ByAnotherAccountWhileAccepting_AnswersInvalidAsync()
    {
        var invitation = await AddInvitationAsync(_invitee);
        using var accepted = await AcceptAsync(_invitee, invitation.Code);

        using var response = await AcceptAsync(Bob, invitation.Code);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadJsonAsync(response))["code"]!.GetValue<string>().Should().Be("directory.invitation_invalid");
        (await ScalarAsync<Guid>("SELECT accepting_account_id FROM directory.invitations WHERE invitation_id = $1", invitation.Id)).Should().Be(_inviteeId);
    }

    [Fact]
    public async Task Accept_WithAnotherAddress_IsRefusedWithoutRevealingItAndDoesNotConsumeAsync()
    {
        var invitation = await AddInvitationAsync(_invitee);

        using var refused = await AcceptAsync(Bob, invitation.Code);
        var refusal = await refused.Content.ReadAsStringAsync(CancellationToken);
        using var invited = await AcceptAsync(_invitee, invitation.Code);

        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        JsonNode.Parse(refusal)!["code"]!.GetValue<string>().Should().Be("directory.invitation_email_mismatch");
        refusal.Should().NotContain("invitee-", "the invited address is never revealed").And.NotContain(invitation.Code);
        invited.StatusCode.Should().Be(HttpStatusCode.Accepted, "the refusal left the invitation pending");
    }

    public static TheoryData<string> UnusableCodes => new() { "unknown", "revoked", "used", "failed", "provisioning", "malformed" };

    [Theory]
    [MemberData(nameof(UnusableCodes))]
    public async Task Accept_ForACodeThatCannotBeUsed_AnswersTheSameInvalidAndChangesNothingAsync(string kind)
    {
        var invitation = await AddInvitationAsync(_invitee);
        var code = kind switch
        {
            "unknown" => NewCode(),
            "malformed" => "not a code",
            _ => invitation.Code,
        };
        var sql = kind switch
        {
            // Used by someone else: only the accepting account gets the repeat answer.
            "used" => "UPDATE directory.invitations SET status = 'used', accepting_account_id = invitation_id, accepted_at = now(), used_at = now() WHERE invitation_id = $1",
            "failed" => "UPDATE directory.invitations SET status = 'failed', failure_code = 'organization.inviter_not_authorized' WHERE invitation_id = $1",
            "provisioning" => "UPDATE directory.organizations SET status = 'provisioning' WHERE id = (SELECT org_id FROM directory.invitations WHERE invitation_id = $1)",
            "revoked" => "UPDATE directory.invitations SET status = 'revoked', revoked_at = now() WHERE invitation_id = $1",
            _ => null,
        };
        if (sql is not null)
        {
            await ExecuteAsync(sql, invitation.Id);
        }

        var status = await ScalarAsync<string>("SELECT status FROM directory.invitations WHERE invitation_id = $1", invitation.Id);

        using var response = await AcceptAsync(_invitee, code);
        using var reference = await AcceptAsync(_invitee, NewCode());

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await ReadJsonAsync(response);
        body["code"]!.GetValue<string>().Should().Be("directory.invitation_invalid");
        body["detail"]!.GetValue<string>().Should().Be((await ReadJsonAsync(reference))["detail"]!.GetValue<string>());
        (await ScalarAsync<string>("SELECT status FROM directory.invitations WHERE invitation_id = $1", invitation.Id)).Should().Be(status);
        (await SentAsync()).Should().BeEmpty();
        (await MembershipAsync(invitation.OrganizationId, _inviteeId)).Should().BeNull();
    }

    [Fact]
    public async Task Accept_WithAnUnverifiedEmail_IsForbiddenAndDoesNotConsumeAsync()
    {
        // The realm requires a verified email to sign in at all, so this runs the service with such an account.
        var invitation = await AddInvitationAsync(_invitee);
        await using var scope = _controlPlane.Services.CreateAsyncScope();
        var acceptance = ActivatorUtilities.CreateInstance<InvitationAcceptance>(scope.ServiceProvider, new StubAccount(_inviteeId, VerifiedEmail: null));

        var result = await acceptance.AcceptAsync(invitation.Code, CancellationToken);

        result.Error!.Code.Should().Be("directory.email_not_verified");
        result.Error.Description.Should().Contain("invitation");
        (await ScalarAsync<string>("SELECT status FROM directory.invitations WHERE invitation_id = $1", invitation.Id)).Should().Be("pending");
    }

    [Fact]
    public async Task Accept_WithoutTheFeatureFlag_DoesNotExistAsync()
    {
        await using var controlPlane = await DirectoryUnderTest.StartAsync(database, keycloak, CancellationToken, invitations: false);
        using var client = controlPlane.GetTestClient();
        var token = await TokenAsync(_invitee);

        using var accept = await AcceptWithTokenAsync(client, token, NewCode());
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/v1/invitations/acceptance", UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var acceptance = await client.SendAsync(request, CancellationToken);

        accept.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await accept.Content.ReadAsStringAsync(CancellationToken)).Should().NotContain("invitation_invalid");
        acceptance.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await acceptance.Content.ReadAsStringAsync(CancellationToken)).Should().NotContain("acceptance_not_found");
    }

    [Fact]
    public async Task Accept_ForAnAccountThatIsAlreadyAnActiveMember_KeepsItsMembershipAsync()
    {
        var invitation = await AddInvitationAsync(_invitee);
        await AddMembershipAsync(invitation.OrganizationId, _inviteeId, "active");

        using var response = await AcceptAsync(_invitee, invitation.Code);
        await SettleAsync(_cellId, invitation, rejectedCode: "organization.inviter_not_authorized");

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await MembershipAsync(invitation.OrganizationId, _inviteeId)).Should().Be("active", "an acceptance never downgrades a membership, and its failure removes only one it added");
    }

    [Fact]
    public async Task Accept_ForAFormerMember_MakesTheMembershipPendingAndARefusalRemovesItAgainAsync()
    {
        var invitation = await AddInvitationAsync(_invitee);
        await AddMembershipAsync(invitation.OrganizationId, _inviteeId, "removed");

        using var response = await AcceptAsync(_invitee, invitation.Code);
        var pending = await MembershipAsync(invitation.OrganizationId, _inviteeId);
        await SettleAsync(_cellId, invitation, rejectedCode: "organization.member_deactivated");

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        pending.Should().Be("pending");
        (await MembershipAsync(invitation.OrganizationId, _inviteeId)).Should().Be("removed");
    }

    [Fact]
    public async Task CellAcknowledgement_MarksTheInvitationUsedAndTheMembershipActiveAsync()
    {
        var invitation = await AddInvitationAsync(_invitee);
        using var accepted = await AcceptAsync(_invitee, invitation.Code);

        await SettleAsync(_cellId, invitation, rejectedCode: null);

        (await ScalarAsync<string>("SELECT status FROM directory.invitations WHERE invitation_id = $1", invitation.Id)).Should().Be("used");
        (await ScalarAsync<DateTime?>("SELECT used_at FROM directory.invitations WHERE invitation_id = $1", invitation.Id)).Should().NotBeNull();
        (await MembershipAsync(invitation.OrganizationId, _inviteeId)).Should().Be("active");
        (await LatestAcceptanceAsync(_invitee)).Should().Be(new InvitationAcceptanceResponse(invitation.OrganizationId, "used", null));
    }

    [Fact]
    public async Task CellAcknowledgement_AfterARevocationWasRequested_StillMarksItUsedAsync()
    {
        var invitation = await AddInvitationAsync(_invitee);
        using var accepted = await AcceptAsync(_invitee, invitation.Code);
        await ExecuteAsync("UPDATE directory.invitations SET revoke_requested_at = now() WHERE invitation_id = $1", invitation.Id);

        await SettleAsync(_cellId, invitation, rejectedCode: null);

        (await ScalarAsync<string>("SELECT status FROM directory.invitations WHERE invitation_id = $1", invitation.Id)).Should().Be("used", "the cell's answer wins");
        (await MembershipAsync(invitation.OrganizationId, _inviteeId)).Should().Be("active");
    }

    [Fact]
    public async Task CellRejection_FailsTheInvitationWithTheCodeAndRemovesTheAddedMembershipAsync()
    {
        var invitation = await AddInvitationAsync(_invitee);
        using var accepted = await AcceptAsync(_invitee, invitation.Code);

        await SettleAsync(_cellId, invitation, rejectedCode: "organization.inviter_not_authorized");
        using var again = await AcceptAsync(_invitee, invitation.Code);

        (await ScalarAsync<string>("SELECT status FROM directory.invitations WHERE invitation_id = $1", invitation.Id)).Should().Be("failed");
        (await MembershipAsync(invitation.OrganizationId, _inviteeId)).Should().Be("removed");
        (await LatestAcceptanceAsync(_invitee)).Should().Be(
            new InvitationAcceptanceResponse(invitation.OrganizationId, "failed", "organization.inviter_not_authorized"));
        again.StatusCode.Should().Be(HttpStatusCode.NotFound, "a failed invitation cannot be accepted again");
    }

    [Fact]
    public async Task Accept_AtTheExpiryByTheControlPlanesClock_AnswersInvalidAsync()
    {
        // The control plane's clock decides, not the database's: the invitation expired an hour ago by the database,
        // and the app's clock stands exactly at the expiry, then just before it. (In the past, so the acceptance it
        // records is never the account's latest in other tests.)
        var expiresAt = DateTimeOffset.UtcNow.AddHours(-1);
        var clock = new FixedTimeProvider(expiresAt);
        await using var controlPlane = await DirectoryUnderTest.StartAsync(
            database, keycloak, CancellationToken, timeProvider: clock, emailHmacKey: Convert.ToBase64String(_emailHmacKey));
        using var client = controlPlane.GetTestClient();
        var invitation = await AddInvitationAsync(_invitee, expiresAt: expiresAt);
        var token = await TokenAsync(_invitee);

        using var expired = await AcceptWithTokenAsync(client, token, invitation.Code);
        clock.Now = expiresAt.AddMilliseconds(-1);
        using var inTime = await AcceptWithTokenAsync(client, token, invitation.Code);

        expired.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadJsonAsync(expired))["code"]!.GetValue<string>().Should().Be("directory.invitation_invalid");
        inTime.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await SentAsync()).Should().ContainSingle().Which.AcceptedAt.ToDateTimeOffset().Should().BeCloseTo(clock.Now, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task Accept_WithACodeAResendReplacedWhileWaitingForTheLock_AnswersInvalidAsync()
    {
        var invitation = await AddInvitationAsync(_invitee);
        var token = await TokenAsync(_invitee);
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(CancellationToken);
        await RunAsync(connection, transaction, "SELECT pg_advisory_xact_lock(1229870676, hashtext($1::text))", invitation.Id);

        // The acceptance has found the invitation by its old code and waits for the lock a resend holds.
        var accept = AcceptWithTokenAsync(_client, token, invitation.Code);
        await WaitForLockWaiterAsync(invitation.Id);
        await RunAsync(connection, transaction, "UPDATE directory.invitations SET code_hash = $2, code_version = 2 WHERE invitation_id = $1", invitation.Id, SHA256.HashData(Encoding.ASCII.GetBytes(NewCode())));
        await transaction.CommitAsync(CancellationToken);
        using var response = await accept;

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "the old link stopped working with the resend");
        (await ScalarAsync<string>("SELECT status FROM directory.invitations WHERE invitation_id = $1", invitation.Id)).Should().Be("pending");
        (await SentAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task TwoAcceptancesOfTheSameAccount_WhenTheCellRefusesOneAndAddsTheOther_LeaveTheMembershipActiveAsync()
    {
        // The cell revoked A and sent B to the same address before the control plane learned of the revocation.
        var first = await AddInvitationAsync(_invitee);
        var second = await AddInvitationAsync(_invitee, organization: first.OrganizationId);
        using var acceptedFirst = await AcceptAsync(_invitee, first.Code);
        using var acceptedSecond = await AcceptAsync(_invitee, second.Code);

        await SettleAsync(_cellId, first, rejectedCode: "organization.invitation_not_pending");
        var afterRefusal = await MembershipAsync(first.OrganizationId, _inviteeId);
        await SettleAsync(_cellId, second, rejectedCode: null);

        acceptedSecond.StatusCode.Should().Be(HttpStatusCode.Accepted);
        afterRefusal.Should().Be("pending", "the other acceptance is still waiting for the cell");
        (await MembershipAsync(first.OrganizationId, _inviteeId)).Should().Be("active");
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/v1/me/organizations", UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(_invitee));
        using var mine = await _client.SendAsync(request, CancellationToken);
        (await mine.Content.ReadAsStringAsync(CancellationToken)).Should().Contain(first.OrganizationId.ToString("D"));
    }

    [Fact]
    public async Task TwoAcceptancesOfTheSameAccount_WhenTheCellRefusesBoth_RemoveTheMembershipAsync()
    {
        var first = await AddInvitationAsync(_invitee);
        var second = await AddInvitationAsync(_invitee, organization: first.OrganizationId);
        using var acceptedFirst = await AcceptAsync(_invitee, first.Code);
        using var acceptedSecond = await AcceptAsync(_invitee, second.Code);

        await SettleAsync(_cellId, first, rejectedCode: "organization.invitation_not_pending");
        await SettleAsync(_cellId, second, rejectedCode: "organization.inviter_not_authorized");

        (await MembershipAsync(first.OrganizationId, _inviteeId)).Should().Be("removed");
    }

    [Fact]
    public async Task CellAcknowledgement_ForAMembershipRemovedMeanwhile_MakesItActiveAsync()
    {
        var invitation = await AddInvitationAsync(_invitee);
        using var accepted = await AcceptAsync(_invitee, invitation.Code);
        await ExecuteAsync("UPDATE directory.memberships SET status = 'removed' WHERE org_id = $1", invitation.OrganizationId);

        await SettleAsync(_cellId, invitation, rejectedCode: null);

        (await MembershipAsync(invitation.OrganizationId, _inviteeId)).Should().Be("active", "the cell has added the member");
    }

    [Fact]
    public async Task CellRejection_OfAnAcceptanceThatFoundAPendingMembership_LeavesItAsync()
    {
        var invitation = await AddInvitationAsync(_invitee);
        await AddMembershipAsync(invitation.OrganizationId, _inviteeId, "pending");

        using var accepted = await AcceptAsync(_invitee, invitation.Code);
        await SettleAsync(_cellId, invitation, rejectedCode: "organization.inviter_not_authorized");

        (await ScalarAsync<bool>("SELECT added_membership FROM directory.invitations WHERE invitation_id = $1", invitation.Id)).Should().BeFalse();
        (await MembershipAsync(invitation.OrganizationId, _inviteeId)).Should().Be("pending", "the acceptance did not add it");
    }

    [Fact]
    public async Task DeliveryExpiry_FailsTheInvitationAndRemovesTheAddedMembershipAsync()
    {
        var invitation = await AddInvitationAsync(_invitee);
        using var accepted = await AcceptAsync(_invitee, invitation.Code);

        // The link tells the handler of a message the cell never answered as a rejection with this code.
        await SettleAsync(_cellId, invitation, rejectedCode: "delivery_expired");

        (await LatestAcceptanceAsync(_invitee)).Should().Be(new InvitationAcceptanceResponse(invitation.OrganizationId, "failed", "delivery_expired"));
        (await MembershipAsync(invitation.OrganizationId, _inviteeId)).Should().Be("removed");
    }

    [Fact]
    public async Task CellAnswer_FromAnotherCell_ChangesNothingAsync()
    {
        var invitation = await AddInvitationAsync(_invitee);
        using var accepted = await AcceptAsync(_invitee, invitation.Code);

        await SettleAsync("another-cell", invitation, rejectedCode: null);
        await SettleAsync("another-cell", invitation, rejectedCode: "organization.invitation_not_pending");

        (await ScalarAsync<string>("SELECT status FROM directory.invitations WHERE invitation_id = $1", invitation.Id)).Should().Be("accepting");
        (await MembershipAsync(invitation.OrganizationId, _inviteeId)).Should().Be("pending");
    }

    [Fact]
    public async Task GetAcceptance_WhileAccepting_ShowsOnlyTheOrganizationAndStateAsync()
    {
        var invitation = await AddInvitationAsync(_invitee);
        using var accepted = await AcceptAsync(_invitee, invitation.Code);

        using var response = await GetAcceptanceAsync(await TokenAsync(_invitee));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        var text = await response.Content.ReadAsStringAsync(CancellationToken);
        text.Should().NotContain("invitee-").And.NotContain(invitation.Code);
        var body = JsonNode.Parse(text)!;
        body["organizationId"]!.GetValue<Guid>().Should().Be(invitation.OrganizationId);
        body["status"]!.GetValue<string>().Should().Be("accepting");
        body["code"].Should().BeNull();
    }

    [Fact]
    public async Task GetAcceptance_ForAnAccountWithoutOne_IsNotFoundAsync()
    {
        await using var scope = _controlPlane.Services.CreateAsyncScope();
        var acceptance = ActivatorUtilities.CreateInstance<InvitationAcceptance>(scope.ServiceProvider, new StubAccount(Guid.CreateVersion7(), _invitee));

        var result = await acceptance.GetLatestAsync(CancellationToken);

        result.Error!.Code.Should().Be("directory.invitation_acceptance_not_found");
    }

    [Fact]
    public async Task GetAcceptance_PolledOftenerThanTheAttemptLimit_IsNotRateLimitedAsync()
    {
        var token = await TokenAsync(_invitee);
        for (var poll = 0; poll < 15; poll++)
        {
            using var response = await GetAcceptanceAsync(token);
            response.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);
        }
    }

    // What the link does when the cell answers: the outcome handler in a unit of work.
    private async Task SettleAsync(string cellId, Invitation invitation, string? rejectedCode)
    {
        await using var scope = _controlPlane.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await unitOfWork.BeginAsync(CancellationToken);
        var message = new CellMessage<InvitationAccepted>(
            Guid.CreateVersion7(),
            cellId,
            new InvitationAccepted { OrganizationId = invitation.OrganizationId.ToString("D"), InvitationId = invitation.Id.ToString("D") });
        foreach (var handler in scope.ServiceProvider.GetServices<ICellMessageOutcomeHandler<InvitationAccepted>>())
        {
            if (rejectedCode is null)
            {
                await handler.OnAcknowledgedAsync(message, CancellationToken);
            }
            else
            {
                await handler.OnRejectedAsync(message, rejectedCode, CancellationToken);
            }
        }

        await unitOfWork.CommitAsync(CancellationToken);
    }

    private async Task<InvitationAcceptanceResponse> LatestAcceptanceAsync(string username)
    {
        using var response = await GetAcceptanceAsync(await TokenAsync(username));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<InvitationAcceptanceResponse>(CancellationToken))!;
    }

    // An invitation as a cell registers it, in an active organization of this test's cell.
    private async Task<Invitation> AddInvitationAsync(string email, Guid? organization = null, DateTimeOffset? expiresAt = null)
    {
        var organizationId = organization ?? Guid.CreateVersion7();
        if (organization is null)
        {
            await ExecuteAsync(
                "INSERT INTO directory.organizations (id, name, region, cell_id, status, created_by, created_at) VALUES ($1, 'Acme', 'eu', $2, 'active', $1, now())",
                organizationId, _cellId);
        }

        var invitationId = Guid.CreateVersion7();
        var code = NewCode();
        await ExecuteAsync(
            """
            INSERT INTO directory.invitations
                (invitation_id, org_id, cell_id, code_hash, email_hmac, role, expires_at, status, code_version, created_at, updated_at)
            VALUES ($1, $2, $3, $4, $5, 'member', $6, 'pending', 1, now(), now())
            """,
            invitationId,
            organizationId,
            _cellId,
            SHA256.HashData(Encoding.ASCII.GetBytes(code)),
            EmailHmac(invitationId, email),
            expiresAt ?? DateTimeOffset.UtcNow.AddDays(7));
        return new Invitation(invitationId, organizationId, code);
    }

    private Task AddMembershipAsync(Guid organizationId, Guid accountId, string status) =>
        ExecuteAsync(
            "INSERT INTO directory.memberships (account_id, org_id, status, version, updated_at) VALUES ($1, $2, $3, 3, now())",
            accountId, organizationId, status);

    private async Task<string?> MembershipAsync(Guid organizationId, Guid accountId)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand("SELECT status FROM directory.memberships WHERE org_id = $1 AND account_id = $2", connection);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organizationId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = accountId });
        return await command.ExecuteScalarAsync(CancellationToken) as string;
    }

    private async Task<IReadOnlyList<InvitationAccepted>> SentAsync()
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT payload FROM cell_management.outbound_messages WHERE cell_id = $1 AND type = 'InvitationAccepted' ORDER BY sequence", connection);
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = _cellId });
        await using var reader = await command.ExecuteReaderAsync(CancellationToken);
        var messages = new List<InvitationAccepted>();
        while (await reader.ReadAsync(CancellationToken))
        {
            messages.Add(ConnectResponse.Parser.ParseFrom(reader.GetFieldValue<byte[]>(0)).InvitationAccepted);
        }

        return messages;
    }

    private byte[] EmailHmac(Guid invitationId, string email)
    {
        var message = new byte[16 + email.Length];
        invitationId.TryWriteBytes(message, bigEndian: true, out _);
        Encoding.ASCII.GetBytes(email, message.AsSpan(16));
        return HMACSHA256.HashData(_emailHmacKey, message);
    }

    private static string NewCode() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private async Task<string> TokenAsync(string username) => (await keycloak.SignInAsync(username, "openid control-plane")).AccessToken;

    private async Task<HttpResponseMessage> AcceptAsync(string username, string code) => await AcceptWithTokenAsync(_client, await TokenAsync(username), code);

    private static async Task<HttpResponseMessage> AcceptWithTokenAsync(HttpClient client, string token, string code)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/invitations/accept", UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(new { code });
        return await client.SendAsync(request, CancellationToken);
    }

    private async Task<HttpResponseMessage> GetAcceptanceAsync(string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/v1/invitations/acceptance", UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(request, CancellationToken);
    }

    private static async Task<JsonNode> ReadJsonAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync(CancellationToken))!;

    [SuppressMessage("Security", "CA2100", Justification = "Test SQL is constant; values are parameters.")]
    private async Task ExecuteAsync(string sql, params object[] values)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }

        await command.ExecuteNonQueryAsync(CancellationToken);
    }

    [SuppressMessage("Security", "CA2100", Justification = "Test SQL is constant; values are parameters.")]
    private async Task<T?> ScalarAsync<T>(string sql, Guid value)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = value });
        return await command.ExecuteScalarAsync(CancellationToken) is T result ? result : default;
    }

    [SuppressMessage("Security", "CA2100", Justification = "Test SQL is constant; values are parameters.")]
    private static async Task RunAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, params object[] values)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }

        await command.ExecuteNonQueryAsync(CancellationToken);
    }

    // Until a session waits for the invitation's advisory lock (class "INVT", key hashtext of the id).
    private async Task WaitForLockWaiterAsync(Guid invitationId)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT EXISTS (SELECT 1 FROM pg_locks
                           WHERE locktype = 'advisory' AND NOT granted AND classid = 1229870676::oid
                             AND objid = (hashtext($1::text)::bigint & 4294967295)::oid)
            """,
            connection);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = invitationId });
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (await command.ExecuteScalarAsync(CancellationToken) is not true)
        {
            DateTimeOffset.UtcNow.Should().BeBefore(deadline, "the acceptance should be waiting for the lock");
            await Task.Delay(TimeSpan.FromMilliseconds(20), CancellationToken);
        }
    }

    private sealed record Invitation(Guid Id, Guid OrganizationId, string Code);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class StubAccount(Guid accountId, string? VerifiedEmail) : ICurrentAccount
    {
        public Guid AccountId => accountId;

        public string? SessionId => "session";

        string? ICurrentAccount.VerifiedEmail => VerifiedEmail;

        public string? DisplayName => "Invitee";

        public string? Locale => null;
    }
}
