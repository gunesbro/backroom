using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Backroom.BuildingBlocks.Application.Persistence;
using Backroom.ControlPlane.Contracts.V1;
using Backroom.Modules.CellManagement.Contracts;
using Backroom.Modules.Directory.Application.Invitations;
using Backroom.Testing;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;

namespace Backroom.Modules.Directory.IntegrationTests;

/// <summary>
/// The control plane's side of invitation registration (docs/contracts/phase-1.md, flow 3, steps 2 and 8, and
/// "States"): the handlers of <c>InvitationRegistered</c> and <c>InvitationRevoked</c>, run in a unit of work as
/// the link runs them, against real PostgreSQL. The link has already checked that the organization is the cell's.
/// </summary>
[Trait("Category", "Privacy")]
public sealed class InvitationRegistryTests(PostgresDatabase database, KeycloakRealm keycloak) : IAsyncLifetime
{
    private const string Cell = "cell-registry";

    private WebApplication _controlPlane = null!;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() =>
        _controlPlane = await DirectoryUnderTest.StartAsync(database, keycloak, CancellationToken);

    public async ValueTask DisposeAsync() => await _controlPlane.DisposeAsync();

    [Fact]
    public async Task Registered_ForANewInvitation_StoresItPendingWithoutTheEmailAsync()
    {
        var organization = await AddOrganizationAsync();
        var message = Registration(organization, email: "kim@example.test");

        (await HandleAsync(message)).Should().BeNull();

        var row = await RowAsync(message.InvitationId);
        row["status"].Should().Be("pending");
        row["org_id"].Should().Be(organization);
        row["cell_id"].Should().Be(Cell);
        row["code_hash"].Should().BeEquivalentTo(Convert.FromHexString(message.CodeHash));
        row["role"].Should().Be("member");
        row["code_version"].Should().Be(1);
        ((DateTime)row["expires_at"]!).Should().BeCloseTo(message.ExpiresAt.ToDateTime(), TimeSpan.FromMilliseconds(1));
        var hmac = (byte[])row["email_hmac"]!;
        hmac.Should().HaveCount(32);
        hmac.Should().NotEqual(SHA256.HashData(Encoding.UTF8.GetBytes("kim@example.test")), "it is keyed, not a plain hash");
        hmac.Should().Equal(Hmac().TryCompute(Guid.Parse(message.InvitationId), "kim@example.test"));
    }

    [Fact]
    public async Task Registered_TheSameEmailInTwoInvitations_GetsDifferentHmacsAsync()
    {
        var organization = await AddOrganizationAsync();
        var first = Registration(organization, email: "kim@example.test");
        var second = Registration(organization, email: "kim@example.test");
        await HandleAsync(first);
        await HandleAsync(second);

        var firstHmac = (byte[])(await RowAsync(first.InvitationId))["email_hmac"]!;
        var secondHmac = (byte[])(await RowAsync(second.InvitationId))["email_hmac"]!;

        secondHmac.Should().NotEqual(firstHmac, "rows must not link one person across invitations without the key");
    }

    [Theory]
    [InlineData("  KIM@Example.TEST ")]
    [InlineData("kim@EXAMPLE.test")]
    public void EmailHmac_ForTheSameAddressWrittenDifferently_IsTheSame(string spelling)
    {
        var invitation = Guid.CreateVersion7();
        var hmac = Hmac();

        var expected = hmac.TryCompute(invitation, "kim@example.test");
        byte[]? underTurkish;
        using (CultureScope.Turkish())
        {
            // A culture-sensitive lower case would turn I into a dotless ı here.
            underTurkish = hmac.TryCompute(invitation, spelling);
        }

        expected.Should().NotBeNull();
        hmac.TryCompute(invitation, spelling).Should().Equal(expected);
        underTurkish.Should().Equal(expected);
    }

    [Fact]
    public void EmailHmac_ForAUnicodeDomain_MatchesItsPunycodeAsTheCellSendsIt()
    {
        var invitation = Guid.CreateVersion7();
        var hmac = Hmac();

        // The cell sends the punycode form; an account may hold the Unicode one.
        hmac.TryCompute(invitation, "kim@münchen.test").Should().Equal(hmac.TryCompute(invitation, "kim@xn--mnchen-3ya.test"));
    }

    [Theory]
    [InlineData("a b@example.test")]
    [InlineData("a,b@example.test")]
    [InlineData("x@y@example.test")]
    [InlineData("<kim@example.test>")]
    [InlineData("kim@localhost")]
    [InlineData("çelik@example.test")]
    public void EmailHmac_ForAnythingButOneAddress_IsNull(string email) =>
        Hmac().TryCompute(Guid.CreateVersion7(), email).Should().BeNull();

    [Fact]
    public async Task Registered_WithANewerCodeVersion_ReplacesOnlyTheHashAsync()
    {
        var organization = await AddOrganizationAsync();
        var first = Registration(organization);
        await HandleAsync(first);
        var resend = first.Clone();
        resend.CodeHash = RandomHash();
        resend.CodeVersion = 2;
        resend.Role = "admin";
        resend.Email = "someone.else@example.test";

        resend.ExpiresAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddDays(1));
        var before = await RowAsync(first.InvitationId);

        (await HandleAsync(resend)).Should().BeNull();

        var row = await RowAsync(first.InvitationId);
        row["code_hash"].Should().BeEquivalentTo(Convert.FromHexString(resend.CodeHash));
        row["code_version"].Should().Be(2);
        row["role"].Should().Be("member", "a resend changes only the code");
        row["email_hmac"].Should().BeEquivalentTo(before["email_hmac"]);
        row["expires_at"].Should().Be(before["expires_at"]);
        row["status"].Should().Be("pending");
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    public async Task Registered_WithAnOlderOrTheSameCodeVersion_IsIgnoredAsync(uint version)
    {
        var organization = await AddOrganizationAsync();
        var current = Registration(organization);
        current.CodeVersion = 2;
        await HandleAsync(current);
        var stale = current.Clone();
        stale.CodeHash = RandomHash();
        stale.CodeVersion = version;

        (await HandleAsync(stale)).Should().BeNull("a late message is acknowledged and dropped");

        (await RowAsync(current.InvitationId))["code_hash"].Should().BeEquivalentTo(Convert.FromHexString(current.CodeHash));
    }

    [Theory]
    [InlineData("accepting")]
    [InlineData("used")]
    [InlineData("failed")]
    [InlineData("expired")]
    public async Task Registered_ForAnInvitationThatIsNoLongerPending_ChangesNothingAsync(string status)
    {
        var organization = await AddOrganizationAsync();
        var first = Registration(organization);
        await HandleAsync(first);
        await SetStatusAsync(first.InvitationId, status);
        var resend = first.Clone();
        resend.CodeHash = RandomHash();
        resend.CodeVersion = 2;

        (await HandleAsync(resend)).Should().BeNull();

        var row = await RowAsync(first.InvitationId);
        row["status"].Should().Be(status);
        row["code_version"].Should().Be(1);
    }

    [Fact]
    public async Task Registered_AfterTheRevocationArrivedFirst_DoesNotRecreateItAsync()
    {
        var organization = await AddOrganizationAsync();
        var registration = Registration(organization);
        (await HandleAsync(Revocation(organization, registration.InvitationId))).Should().BeNull();

        (await HandleAsync(registration)).Should().BeNull();

        var row = await RowAsync(registration.InvitationId);
        row["status"].Should().Be("revoked");
        row["code_hash"].Should().BeNull("a tombstone holds no code");
        row["email_hmac"].Should().BeNull();
    }

    [Fact]
    public async Task Registered_ForAnInvitationOfAnotherOrganization_IsRefusedForGoodAsync()
    {
        var mine = await AddOrganizationAsync();
        var theirs = await AddOrganizationAsync();
        var theirInvitation = Registration(theirs);
        await HandleAsync(theirInvitation);
        var takeover = theirInvitation.Clone();
        takeover.OrganizationId = mine.ToString("D");
        takeover.CodeHash = RandomHash();
        takeover.CodeVersion = 5;

        (await HandleAsync(takeover)).Should().Be("directory.invitation_not_in_organization");

        var row = await RowAsync(theirInvitation.InvitationId);
        row["org_id"].Should().Be(theirs);
        row["code_version"].Should().Be(1);
    }

    [Fact]
    public async Task Registered_WithTheCodeHashOfAnotherInvitation_IsRefusedForGoodAsync()
    {
        var organization = await AddOrganizationAsync();
        var first = Registration(organization);
        await HandleAsync(first);
        var second = Registration(organization);
        second.CodeHash = first.CodeHash;

        (await HandleAsync(second)).Should().Be("directory.invitation_code_conflict");

        (await CountAsync($"SELECT count(*) FROM directory.invitations WHERE invitation_id = '{second.InvitationId}'")).Should().Be(0);
    }

    [Theory]
    [MemberData(nameof(InvalidRegistrations))]
    public async Task Registered_WithAnInvalidField_IsRefusedForGoodAsync(string field, Action<InvitationRegistered> spoil)
    {
        ArgumentNullException.ThrowIfNull(spoil);
        var organization = await AddOrganizationAsync();
        var message = Registration(organization);
        var invitationId = Guid.TryParse(message.InvitationId, out var id) ? id : Guid.Empty;
        spoil(message);

        (await HandleAsync(message)).Should().Be("directory.invitation_message_invalid", field);

        (await CountAsync($"SELECT count(*) FROM directory.invitations WHERE invitation_id = '{invitationId}'")).Should().Be(0);
    }

    public static TheoryData<string, Action<InvitationRegistered>> InvalidRegistrations() => new()
    {
        { "invitation id", message => message.InvitationId = "not-a-guid" },
        { "empty invitation id", message => message.InvitationId = Guid.Empty.ToString("D") },
        { "hash in upper case", message => message.CodeHash = message.CodeHash.ToUpperInvariant() },
        { "hash too short", message => message.CodeHash = message.CodeHash[..62] },
        { "hash not hex", message => message.CodeHash = new string('z', 64) },
        { "owner role", message => message.Role = "owner" },
        { "unknown role", message => message.Role = "Member" },
        { "no email", message => message.Email = string.Empty },
        { "email without at", message => message.Email = "kim.example.test" },
        { "email with line break", message => message.Email = "kim@example.test\r\nBcc: x@example.test" },
        { "code version 0", message => message.CodeVersion = 0 },
        { "no expiry", message => message.ExpiresAt = null },
        { "expiry too far", message => message.ExpiresAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddDays(9)) },
        { "expiry long past", message => message.ExpiresAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddDays(-31)) },
        { "expiry beyond what a timestamp can convert", message => message.ExpiresAt = new Timestamp { Seconds = long.MaxValue } },
        { "expiry with negative nanoseconds", message => message.ExpiresAt = new Timestamp { Seconds = 1, Nanos = -1 } },
        { "email with a non-ASCII local part", message => message.Email = "çelik@example.test" },
        { "two addresses", message => message.Email = "kim@example.test, lee@example.test" },
    };

    [Fact]
    public async Task Registered_AfterTheInvitationWasRevoked_DoesNotBringItBackAsync()
    {
        var organization = await AddOrganizationAsync();
        var registration = Registration(organization);
        await HandleAsync(registration);
        await HandleAsync(Revocation(organization, registration.InvitationId));
        var resend = registration.Clone();
        resend.CodeHash = RandomHash();
        resend.CodeVersion = 2;

        (await HandleAsync(resend)).Should().BeNull();

        var row = await RowAsync(registration.InvitationId);
        row["status"].Should().Be("revoked");
        row["code_version"].Should().Be(1);
    }

    [Fact]
    public async Task Registered_ForATombstoneOfAnotherOrganization_IsRefusedForGoodAsync()
    {
        var first = await AddOrganizationAsync();
        var second = await AddOrganizationAsync();
        var invitationId = Guid.CreateVersion7().ToString("D");
        await HandleAsync(Revocation(first, invitationId));
        var registration = Registration(second);
        registration.InvitationId = invitationId;

        (await HandleAsync(registration)).Should().Be("directory.invitation_not_in_organization");
    }

    [Fact]
    public async Task Revoked_APendingInvitation_RevokesItAsync()
    {
        var organization = await AddOrganizationAsync();
        var registration = Registration(organization);
        await HandleAsync(registration);

        (await HandleAsync(Revocation(organization, registration.InvitationId))).Should().BeNull();

        var row = await RowAsync(registration.InvitationId);
        row["status"].Should().Be("revoked");
        row["revoked_at"].Should().NotBeNull();
        row["code_hash"].Should().NotBeNull("the retention job deletes the invitation's data later");
    }

    [Fact]
    public async Task Revoked_WhileItIsBeingAccepted_OnlyRecordsTheRequestAsync()
    {
        var organization = await AddOrganizationAsync();
        var registration = Registration(organization);
        await HandleAsync(registration);
        await SetStatusAsync(registration.InvitationId, "accepting");

        (await HandleAsync(Revocation(organization, registration.InvitationId))).Should().BeNull();

        var row = await RowAsync(registration.InvitationId);
        row["status"].Should().Be("accepting", "the cell's answer to the acceptance decides");
        row["revoke_requested_at"].Should().NotBeNull();
        row["revoked_at"].Should().BeNull();
    }

    [Fact]
    public async Task Revoked_AgainWhileItIsBeingAccepted_KeepsTheFirstRequestTimeAsync()
    {
        var organization = await AddOrganizationAsync();
        var registration = Registration(organization);
        await HandleAsync(registration);
        await SetStatusAsync(registration.InvitationId, "accepting");
        await HandleAsync(Revocation(organization, registration.InvitationId));
        var first = (await RowAsync(registration.InvitationId))["revoke_requested_at"];

        (await HandleAsync(Revocation(organization, registration.InvitationId))).Should().BeNull();

        (await RowAsync(registration.InvitationId))["revoke_requested_at"].Should().Be(first);
    }

    [Theory]
    [InlineData("used")]
    [InlineData("failed")]
    [InlineData("expired")]
    public async Task Revoked_AnInvitationThatEnded_ChangesNothingAsync(string status)
    {
        var organization = await AddOrganizationAsync();
        var registration = Registration(organization);
        await HandleAsync(registration);
        await SetStatusAsync(registration.InvitationId, status);

        (await HandleAsync(Revocation(organization, registration.InvitationId))).Should().BeNull();

        (await RowAsync(registration.InvitationId))["status"].Should().Be(status);
    }

    [Fact]
    public async Task Revoked_TwiceOrForAnUnknownInvitation_IsAcknowledgedAsync()
    {
        var organization = await AddOrganizationAsync();
        var unknown = Guid.CreateVersion7().ToString("D");

        (await HandleAsync(Revocation(organization, unknown))).Should().BeNull();
        (await HandleAsync(Revocation(organization, unknown))).Should().BeNull();

        var row = await RowAsync(unknown);
        row["status"].Should().Be("revoked");
        row["org_id"].Should().Be(organization);
        row["code_hash"].Should().BeNull("a tombstone holds no invitation data");
        row["email_hmac"].Should().BeNull();
        row["role"].Should().BeNull();
    }

    [Fact]
    public async Task Revoked_ForAnInvitationOfAnotherOrganization_IsRefusedForGoodAsync()
    {
        var mine = await AddOrganizationAsync();
        var theirs = await AddOrganizationAsync();
        var theirInvitation = Registration(theirs);
        await HandleAsync(theirInvitation);

        (await HandleAsync(Revocation(mine, theirInvitation.InvitationId))).Should().Be("directory.invitation_not_in_organization");

        (await RowAsync(theirInvitation.InvitationId))["status"].Should().Be("pending");
    }

    [Fact]
    public async Task Revoked_WithAnInvalidInvitationId_IsRefusedForGoodAsync()
    {
        var organization = await AddOrganizationAsync();

        (await HandleAsync(Revocation(organization, "not-a-guid"))).Should().Be("directory.invitation_message_invalid");
    }

    [Fact]
    public async Task Start_WithoutAnEmailHmacKey_FailsAsync()
    {
        var start = () => DirectoryUnderTest.StartAsync(database, keycloak, CancellationToken, emailHmacKey: string.Empty);

        await start.Should().ThrowAsync<Microsoft.Extensions.Options.OptionsValidationException>();
    }

    [Fact]
    public async Task Start_WithAShortEmailHmacKey_FailsWithoutQuotingItAsync()
    {
        var shortKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(31));
        var start = () => DirectoryUnderTest.StartAsync(database, keycloak, CancellationToken, emailHmacKey: shortKey);

        var failure = await start.Should().ThrowAsync<Microsoft.Extensions.Options.OptionsValidationException>();
        failure.Which.Message.Should().NotContain(shortKey);
    }

    private static InvitationRegistered Registration(Guid organization, string email = "kim@example.test") => new()
    {
        OrganizationId = organization.ToString("D"),
        InvitationId = Guid.CreateVersion7().ToString("D"),
        CodeHash = RandomHash(),
        Email = email,
        Role = "member",
        ExpiresAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddDays(7)),
        CodeVersion = 1,
    };

    private static InvitationRevoked Revocation(Guid organization, string invitationId) =>
        new() { OrganizationId = organization.ToString("D"), InvitationId = invitationId };

    private IInvitationEmailHmac Hmac() => _controlPlane.Services.GetRequiredService<IInvitationEmailHmac>();

    // The columns each state requires come with it.
    private Task SetStatusAsync(string invitationId, string status) =>
        ExecuteAsync(
            """
            UPDATE directory.invitations
            SET status = $2,
                accepting_account_id = CASE WHEN $2 IN ('accepting', 'used') THEN gen_random_uuid() END,
                accepted_at = CASE WHEN $2 IN ('accepting', 'used') THEN now() END,
                failure_code = CASE WHEN $2 = 'failed' THEN 'organization.invitation_expired' END
            WHERE invitation_id = $1
            """,
            Guid.Parse(invitationId),
            status);

    private static string RandomHash() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    /// <summary>Runs the message's handlers as the link does; returns the rejection code, or null if it was accepted.</summary>
    private async Task<string?> HandleAsync<T>(T payload)
        where T : IMessage
    {
        await using var scope = _controlPlane.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await unitOfWork.BeginAsync(CancellationToken);
        var message = new CellMessage<T>(Guid.CreateVersion7(), Cell, payload);
        try
        {
            var handlers = scope.ServiceProvider.GetServices<ICellMessageHandler<T>>().ToList();
            handlers.Should().ContainSingle();
            await handlers[0].HandleAsync(message, CancellationToken);
        }
        catch (CellMessageRejectedException rejected)
        {
            await unitOfWork.RollbackAsync(CancellationToken);
            return rejected.Code;
        }

        await unitOfWork.CommitAsync(CancellationToken);
        return null;
    }

    private async Task<Guid> AddOrganizationAsync()
    {
        var organization = Guid.CreateVersion7();
        await ExecuteAsync(
            """
            INSERT INTO directory.organizations (id, name, region, cell_id, status, created_by, created_at)
            VALUES ($1, 'Acme', 'eu', $2, 'active', $1, now())
            """,
            organization,
            Cell);
        return organization;
    }

    private async Task<Dictionary<string, object?>> RowAsync(string invitationId)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand("SELECT * FROM directory.invitations WHERE invitation_id = $1", connection);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = Guid.Parse(invitationId) });
        await using var reader = await command.ExecuteReaderAsync(CancellationToken);
        (await reader.ReadAsync(CancellationToken)).Should().BeTrue();
        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var index = 0; index < reader.FieldCount; index++)
        {
            row[reader.GetName(index)] = await reader.IsDBNullAsync(index, CancellationToken) ? null : reader.GetValue(index);
        }

        return row;
    }

    [SuppressMessage("Security", "CA2100", Justification = "Test SQL; ids are generated by the test.")]
    private async Task<long> CountAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync(CancellationToken))!;
    }

    [SuppressMessage("Security", "CA2100", Justification = "The SQL is a constant of this test class; values are parameters.")]
    private async Task ExecuteAsync(string sql, params object[] parameters)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        }

        await command.ExecuteNonQueryAsync(CancellationToken);
    }
}
