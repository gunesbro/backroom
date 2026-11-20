using System.Security.Cryptography;
using AwesomeAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Backroom.BuildingBlocks.Application.Persistence;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.ControlPlane.Contracts.V1;
using Backroom.Modules.CellManagement.Contracts;
using Backroom.Modules.ControlPlaneConnector.Infrastructure.Link;
using Backroom.Modules.Organization.Contracts;
using Backroom.Modules.Organization.Infrastructure;
using Backroom.Modules.Permissions.Infrastructure;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;

namespace Backroom.Modules.CellManagement.IntegrationTests;

/// <summary>
/// An accepted invitation reaches its cell (docs/contracts/phase-1.md, flow 3, steps 6 and 7): the control plane sends
/// <c>InvitationAccepted</c>, the cell adds the member in the organization's unit of work and acknowledges; a business
/// rejection becomes a non-retryable <c>Nack</c> with Organization's code, and an invitation the cell does not know
/// yet a retryable one.
/// </summary>
[Collection(LinkWorkerCollection.Name)]
[Trait("Category", "Privacy")]
public sealed class CellInvitationAcceptanceTests(PostgresDatabase database, KeycloakRealm keycloak) : IAsyncLifetime
{
    private WebApplication _controlPlane = null!;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _controlPlane = await ControlPlaneUnderTest.StartAsync(database, keycloak);

    public async ValueTask DisposeAsync() => await _controlPlane.DisposeAsync();

    [Fact]
    public async Task InvitationAccepted_AddsTheMemberAndIsAcknowledgedAsync()
    {
        await using var cell = await StartCellAsync();
        var (organizationId, ownerId) = await ProvisionAsync(cell);
        var invitationId = await SeedInvitationAsync(organizationId, ownerId);
        var accountId = Guid.CreateVersion7();

        var messageId = await EnqueueAsync(cell.CellId, Accepted(organizationId, invitationId, accountId));

        await WaitUntilAsync(async () => await StateAsync(messageId) == "acked");
        (await CountAsync(
            "SELECT count(*) FROM organization.members WHERE org_id = $1 AND account_id = $2 AND status = 'active' AND display_name = 'Kim Test' AND version = 1",
            organizationId, accountId)).Should().Be(1);
        (await CountAsync(
            "SELECT count(*) FROM permissions.member_roles m JOIN permissions.roles r ON r.id = m.role_id WHERE m.org_id = $1 AND m.account_id = $2 AND r.key = 'member'",
            organizationId, accountId)).Should().Be(1);
        (await CountAsync("SELECT count(*) FROM organization.invitations WHERE id = $1 AND status = 'accepted' AND accepted_by = $2", invitationId, accountId)).Should().Be(1);
        (await CountAsync("SELECT count(*) FROM organization.outbox WHERE org_id = $1 AND event_type = 'organization.member-added.v1'", organizationId)).Should().Be(1);
    }

    [Fact]
    public async Task InvitationAccepted_WithAnotherEmail_IsRejectedWithOrganizationsCodeAsync()
    {
        await using var cell = await StartCellAsync();
        var (organizationId, ownerId) = await ProvisionAsync(cell);
        var invitationId = await SeedInvitationAsync(organizationId, ownerId);
        var accountId = Guid.CreateVersion7();
        var message = Accepted(organizationId, invitationId, accountId);
        message.Email = "lee@example.test";

        var messageId = await EnqueueAsync(cell.CellId, message);

        await WaitUntilAsync(async () => await StateAsync(messageId) == "organization.invitation_email_mismatch");
        (await CountAsync("SELECT count(*) FROM organization.members WHERE org_id = $1 AND account_id = $2", organizationId, accountId)).Should().Be(0);
        (await CountAsync("SELECT count(*) FROM organization.invitations WHERE id = $1 AND status = 'pending'", invitationId)).Should().Be(1);
    }

    [Theory]
    [InlineData("invitation id")]
    [InlineData("account id")]
    [InlineData("accepted at")]
    public async Task InvitationAccepted_WithInvalidValues_IsRejectedAsync(string kind)
    {
        await using var cell = await StartCellAsync();
        var (organizationId, ownerId) = await ProvisionAsync(cell);
        var invitationId = await SeedInvitationAsync(organizationId, ownerId);
        var message = Accepted(organizationId, invitationId, Guid.CreateVersion7());
        switch (kind)
        {
            case "invitation id": message.InvitationId = "not-an-invitation"; break;
            case "account id": message.AccountId = ""; break;
            default: message.AcceptedAt = null; break;
        }

        var messageId = await EnqueueAsync(cell.CellId, message);

        await WaitUntilAsync(async () => await StateAsync(messageId) == "organization.invitation_acceptance_invalid");
    }

    [Fact]
    public async Task InvitationAccepted_ForAnInvitationTheCellDoesNotKnowYet_IsRetriedUntilItExistsAsync()
    {
        await using var cell = await StartCellAsync();
        var (organizationId, ownerId) = await ProvisionAsync(cell);
        var invitationId = Guid.CreateVersion7();
        var accountId = Guid.CreateVersion7();

        var messageId = await EnqueueAsync(cell.CellId, Accepted(organizationId, invitationId, accountId));

        await WaitUntilAsync(async () => await AttemptsAsync(messageId) >= 1);
        (await StateAsync(messageId)).Should().Be("pending", "a missing prerequisite is a retryable Nack, never an Ack or a rejection");

        // The retry after the backoff finds it.
        await SeedInvitationAsync(organizationId, ownerId, invitationId);
        await WaitUntilAsync(async () => await StateAsync(messageId) == "acked");
        (await CountAsync("SELECT count(*) FROM organization.members WHERE org_id = $1 AND account_id = $2", organizationId, accountId)).Should().Be(1);
    }

    [Fact]
    public async Task InvitationAccepted_ForAnOrganizationTheCellDoesNotHave_IsRetriedAsync()
    {
        await using var cell = await StartCellAsync();

        var messageId = await EnqueueAsync(cell.CellId, Accepted(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7()));

        await WaitUntilAsync(async () => await AttemptsAsync(messageId) >= 1);
        (await StateAsync(messageId)).Should().Be("pending");
    }

    private async Task<TestCell> StartCellAsync()
    {
        var cell = await TestCell.RegisterAsync(_controlPlane, keycloak, CancellationToken, database, configureServices: services =>
        {
            services.AddPermissions();
            services.AddOrganization();
            services.AddOrganizationInvitations().Configure(options =>
            {
                options.CurrentVersion = 1;
                options.Keys["1"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            });
            services.AddScoped<IDownstreamHandler<InvitationAccepted>, InvitationAcceptedHandler>();
        });
        await cell.Link.StartAsync(CancellationToken);
        await WaitUntilAsync(() => Task.FromResult(cell.Link.IsConnected));
        return cell;
    }

    private static async Task<(Guid Organization, Guid Owner)> ProvisionAsync(TestCell cell)
    {
        var organizationId = Guid.CreateVersion7();
        var ownerId = Guid.CreateVersion7();
        await using var scope = cell.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<BuildingBlocks.Application.Organizations.OrganizationContext>().Enter(organizationId);
        var session = scope.ServiceProvider.GetRequiredService<DatabaseSession>();
        await session.BeginAsync(CancellationToken);
        var result = await scope.ServiceProvider.GetRequiredService<IOrganizationProvisioning>().ProvisionAsync(
            new NewOrganization(organizationId, "Acme", ownerId, "Alice", "alice@example.test", "en-US"), CancellationToken);
        result.IsSuccess.Should().BeTrue();
        await session.CommitAsync(CancellationToken);
        return (organizationId, ownerId);
    }

    private static InvitationAccepted Accepted(Guid organizationId, Guid invitationId, Guid accountId) => new()
    {
        OrganizationId = organizationId.ToString("D"),
        InvitationId = invitationId.ToString("D"),
        AccountId = accountId.ToString("D"),
        DisplayName = "Kim Test",
        Email = "kim@example.test",
        Locale = "en-US",
        AcceptedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
    };

    private async Task<Guid> EnqueueAsync(string cellId, InvitationAccepted message)
    {
        await using var scope = _controlPlane.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await unitOfWork.BeginAsync(CancellationToken);
        var messageId = await scope.ServiceProvider.GetRequiredService<ICellLinkOutbox>().EnqueueAsync(
            cellId, new ConnectResponse { InvitationAccepted = message }, CancellationToken);
        await unitOfWork.CommitAsync(CancellationToken);
        return messageId;
    }

    private async Task<Guid> SeedInvitationAsync(Guid organizationId, Guid createdBy, Guid? invitationId = null)
    {
        var id = invitationId ?? Guid.CreateVersion7();
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO organization.invitations
                (id, org_id, email, role, salt, key_version, code_version, code_hash, status, created_by, created_at, expires_at)
            VALUES ($1, $2, 'kim@example.test', 'member', decode(repeat('00', 16), 'hex'), 1, 1, $3, 'pending', $4, now(), now() + interval '7 days')
            """,
            connection);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organizationId });
        command.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = RandomNumberGenerator.GetBytes(32) });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = createdBy });
        await command.ExecuteNonQueryAsync(CancellationToken);
        return id;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        for (var attempt = 0; attempt < 150 && !await condition(); attempt++)
        {
            await Task.Delay(100, CancellationToken);
        }

        (await condition()).Should().BeTrue();
    }

    private async Task<string?> StateAsync(Guid messageId) =>
        await ScalarAsync<string>(
            "SELECT CASE WHEN acked_at IS NOT NULL THEN 'acked' WHEN rejected_code IS NOT NULL THEN rejected_code ELSE 'pending' END FROM cell_management.outbound_messages WHERE message_id = $1",
            messageId);

    private async Task<int> AttemptsAsync(Guid messageId) =>
        await ScalarAsync<int>("SELECT attempts FROM cell_management.outbound_messages WHERE message_id = $1", messageId);

    private async Task<long> CountAsync(string sql, params Guid[] values) => await ScalarAsync<long>(sql, values);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100", Justification = "Test SQL is constant; values are parameters.")]
    private async Task<T?> ScalarAsync<T>(string sql, params Guid[] values)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = value });
        }

        return await command.ExecuteScalarAsync(CancellationToken) is T result ? result : default;
    }
}
