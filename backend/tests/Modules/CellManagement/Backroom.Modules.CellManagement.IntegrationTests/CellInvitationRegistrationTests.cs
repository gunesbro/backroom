using System.Collections.Concurrent;
using System.Security.Cryptography;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Backroom.BuildingBlocks.Application.Organizations;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.ControlPlane.Contracts.V1;
using Backroom.Modules.CellManagement.Contracts;
using Backroom.Modules.ControlPlaneConnector.Infrastructure.Events;
using Backroom.Modules.ControlPlaneConnector.Infrastructure.Link;
using Backroom.Modules.Organization.Contracts;
using Backroom.Modules.Organization.Infrastructure;
using Backroom.Modules.Organization.Infrastructure.Invitations;
using Backroom.Modules.Permissions.Infrastructure;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;
using LinkInvitationRevoked = Backroom.ControlPlane.Contracts.V1.InvitationRevoked;
using OrganizationInvitationRevoked = Backroom.Modules.Organization.Contracts.InvitationRevoked;

namespace Backroom.Modules.CellManagement.IntegrationTests;

/// <summary>
/// An invitation reaches the control plane (docs/contracts/phase-1.md, flow 3, steps 2 and 3): the connector turns
/// Organization's events into upstream messages with the current code hash, and only once the control plane
/// acknowledged a registration does it mark the invitation registered, which publishes the ready event for the email.
/// </summary>
[Collection(LinkWorkerCollection.Name)]
[Trait("Category", "Privacy")]
public sealed class CellInvitationRegistrationTests(PostgresDatabase database, KeycloakRealm keycloak) : IAsyncLifetime
{
    private readonly ConcurrentDictionary<Guid, string> _organizations = new();
    private readonly ConcurrentQueue<InvitationRegistered> _registered = new();
    private WebApplication _controlPlane = null!;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() =>
        _controlPlane = await ControlPlaneUnderTest.StartAsync(database, keycloak, configureServices: services =>
        {
            services.AddSingleton<ICellOrganizationScope>(new MapScope(_organizations));
            services.AddSingleton<ICellMessageHandler<InvitationRegistered>>(new RegistrationRecorder(_registered));
        });

    public async ValueTask DisposeAsync() => await _controlPlane.DisposeAsync();

    [Fact]
    public async Task InvitationCreated_IsRegisteredUpstreamAndMarkedOnceAcknowledgedAsync()
    {
        await using var cell = await CreateCellAsync();
        var (organizationId, ownerId) = await ProvisionAsync(cell);
        var codeHash = RandomNumberGenerator.GetBytes(32);
        var invitationId = await SeedInvitationAsync(organizationId, ownerId, codeVersion: 1, codeHash);

        await ConsumeAsync(cell, organizationId, Created(organizationId, invitationId, codeVersion: 1));

        var (messageId, message) = (await UpstreamAsync(invitationId)).Should().ContainSingle().Subject;
        message.PayloadCase.Should().Be(ConnectRequest.PayloadOneofCase.InvitationRegistered);
        message.InvitationRegistered.OrganizationId.Should().Be(organizationId.ToString("D"));
        message.InvitationRegistered.CodeHash.Should().Be(Convert.ToHexStringLower(codeHash));
        message.InvitationRegistered.CodeVersion.Should().Be(1);
        message.InvitationRegistered.Email.Should().Be("kim@example.test");
        message.InvitationRegistered.Role.Should().Be("member");
        (await ReadyEventsAsync(organizationId)).Should().Be(0, "the email waits for the control plane");

        await StartAsync(cell);
        try
        {
            await WaitUntilAsync(async () => await UpstreamStateAsync(messageId) == "acked");
            _registered.Should().ContainSingle(registered => registered.InvitationId == invitationId.ToString("D"));
            (await ScalarAsync<bool>("SELECT registered_at IS NOT NULL FROM organization.invitations WHERE id = $1", invitationId)).Should().BeTrue();
            (await ReadyEventsAsync(organizationId)).Should().Be(1);
        }
        finally
        {
            await cell.Link.StopAsync(CancellationToken);
        }
    }

    [Fact]
    public async Task InvitationCreated_ForAVersionAResendReplaced_SendsNothingAsync()
    {
        await using var cell = await CreateCellAsync();
        var (organizationId, ownerId) = await ProvisionAsync(cell);
        var invitationId = await SeedInvitationAsync(organizationId, ownerId, codeVersion: 2, RandomNumberGenerator.GetBytes(32));

        await ConsumeAsync(cell, organizationId, Created(organizationId, invitationId, codeVersion: 1));

        (await UpstreamAsync(invitationId)).Should().BeEmpty("the resend's own event registers version 2");
    }

    [Fact]
    public async Task InvitationCreated_ForARevokedInvitation_SendsNothingAsync()
    {
        await using var cell = await CreateCellAsync();
        var (organizationId, ownerId) = await ProvisionAsync(cell);
        var invitationId = await SeedInvitationAsync(organizationId, ownerId, codeVersion: 1, RandomNumberGenerator.GetBytes(32), status: "revoked");

        await ConsumeAsync(cell, organizationId, Created(organizationId, invitationId, codeVersion: 1));

        (await UpstreamAsync(invitationId)).Should().BeEmpty("its revocation is sent instead");
    }

    [Theory]
    [InlineData("revoked", false)]
    [InlineData("pending", true)]
    public async Task Acknowledgement_ForARevokedOrAlreadyMarkedInvitation_IsSettledWithoutAReadyEventAsync(string status, bool registered)
    {
        await using var cell = await CreateCellAsync();
        var (organizationId, ownerId) = await ProvisionAsync(cell);
        var invitationId = await SeedInvitationAsync(organizationId, ownerId, codeVersion: 1, RandomNumberGenerator.GetBytes(32), status, registered);
        var messageId = await EnqueueRegistrationAsync(cell, organizationId, invitationId);

        await StartAsync(cell);
        try
        {
            await WaitUntilAsync(async () => await UpstreamStateAsync(messageId) == "acked");
            (await ReadyEventsAsync(organizationId)).Should().Be(0, "nothing is left to send");
        }
        finally
        {
            await cell.Link.StopAsync(CancellationToken);
        }
    }

    [Fact]
    public async Task InvitationRevoked_IsSentUpstreamAsync()
    {
        await using var cell = await CreateCellAsync();
        var (organizationId, _) = await ProvisionAsync(cell);
        var invitationId = Guid.CreateVersion7();

        await ConsumeAsync(cell, organizationId, new OrganizationInvitationRevoked(Guid.CreateVersion7(), organizationId, DateTimeOffset.UtcNow, invitationId));

        var (_, message) = (await UpstreamAsync(invitationId)).Should().ContainSingle().Subject;
        message.PayloadCase.Should().Be(ConnectRequest.PayloadOneofCase.InvitationRevoked);
        message.InvitationRevoked.Should().Be(new LinkInvitationRevoked
        {
            OrganizationId = organizationId.ToString("D"),
            InvitationId = invitationId.ToString("D"),
        });
    }

    [Fact]
    public async Task Acknowledgement_WhenMarkingTheInvitationFails_IsNotSettledAndIsSentAgainAsync()
    {
        var failing = new FailureLog();
        await using var cell = await CreateCellAsync(services =>
            services.AddScoped<IInvitationRegistration>(provider => new FailingRegistration(failing, provider.GetRequiredService<IOrganizationContext>())));
        var organizationId = Guid.CreateVersion7();
        _organizations[organizationId] = cell.CellId;
        var invitationId = Guid.CreateVersion7();
        var messageId = await EnqueueRegistrationAsync(cell, organizationId, invitationId);

        await StartAsync(cell);
        try
        {
            await WaitUntilAsync(async () => failing.Calls >= 1 && await AttemptsAsync(messageId) >= 1);
            (await UpstreamStateAsync(messageId)).Should().Be("pending", "settling would lose the email for good");
            (await HasPayloadAsync(messageId)).Should().BeTrue();
            failing.Organizations.Should().AllBeEquivalentTo(organizationId, "the outcome runs in the invitation's organization");
        }
        finally
        {
            await cell.Link.StopAsync(CancellationToken);
        }
    }

    private async Task<TestCell> CreateCellAsync(Action<IServiceCollection>? configure = null) =>
        await TestCell.RegisterAsync(_controlPlane, keycloak, CancellationToken, database, configureServices: services =>
        {
            // Before the module, whose registrations do not replace one that exists.
            configure?.Invoke(services);
            services.AddPermissions();
            services.AddOrganization();
            services.AddOrganizationInvitations().Configure(options =>
            {
                options.CurrentVersion = 1;
                options.Keys["1"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            });
            services.AddScoped<InvitationCreatedConsumer>();
            services.AddScoped<InvitationRevokedConsumer>();
        });

    private static async Task StartAsync(TestCell cell)
    {
        await cell.Link.StartAsync(CancellationToken);
        await WaitUntilAsync(() => Task.FromResult(cell.Link.IsConnected));
    }

    private async Task<(Guid Organization, Guid Owner)> ProvisionAsync(TestCell cell)
    {
        var organizationId = Guid.CreateVersion7();
        var ownerId = Guid.CreateVersion7();
        _organizations[organizationId] = cell.CellId;
        await InOrganizationAsync(cell, organizationId, async services =>
        {
            var result = await services.GetRequiredService<IOrganizationProvisioning>().ProvisionAsync(
                new NewOrganization(organizationId, "Acme", ownerId, "Alice", "alice@example.test", "en-US"), CancellationToken);
            result.IsSuccess.Should().BeTrue();
        });
        return (organizationId, ownerId);
    }

    private static InvitationCreated Created(Guid organizationId, Guid invitationId, int codeVersion) =>
        new(Guid.CreateVersion7(), organizationId, DateTimeOffset.UtcNow, invitationId, codeVersion, "kim@example.test", "member", DateTimeOffset.UtcNow.AddDays(7));

    private static Task ConsumeAsync(TestCell cell, Guid organizationId, InvitationCreated integrationEvent) =>
        InOrganizationAsync(cell, organizationId, services =>
            services.GetRequiredService<InvitationCreatedConsumer>().HandleAsync(integrationEvent, CancellationToken));

    private static Task ConsumeAsync(TestCell cell, Guid organizationId, OrganizationInvitationRevoked integrationEvent) =>
        InOrganizationAsync(cell, organizationId, services =>
            services.GetRequiredService<InvitationRevokedConsumer>().HandleAsync(integrationEvent, CancellationToken));

    // The way the consumer host runs a handler: a unit of work in the event's organization.
    private static async Task InOrganizationAsync(TestCell cell, Guid organizationId, Func<IServiceProvider, Task> work)
    {
        await using var scope = cell.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<OrganizationContext>().Enter(organizationId);
        var session = scope.ServiceProvider.GetRequiredService<DatabaseSession>();
        await session.BeginAsync(CancellationToken);
        await work(scope.ServiceProvider);
        await session.CommitAsync(CancellationToken);
    }

    private static async Task<Guid> EnqueueRegistrationAsync(TestCell cell, Guid organizationId, Guid invitationId)
    {
        await using var scope = cell.Services.CreateAsyncScope();
        var session = scope.ServiceProvider.GetRequiredService<DatabaseSession>();
        await session.BeginAsync(CancellationToken);
        var messageId = await scope.ServiceProvider.GetRequiredService<UpstreamOutbox>().EnqueueAsync(
            new ConnectRequest
            {
                InvitationRegistered = new InvitationRegistered
                {
                    OrganizationId = organizationId.ToString("D"),
                    InvitationId = invitationId.ToString("D"),
                    CodeHash = new string('0', 64),
                    Email = "kim@example.test",
                    Role = "member",
                    ExpiresAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddDays(7)),
                    CodeVersion = 1,
                },
            },
            CancellationToken);
        await session.CommitAsync(CancellationToken);
        return messageId;
    }

    private async Task<Guid> SeedInvitationAsync(Guid organizationId, Guid createdBy, int codeVersion, byte[] codeHash, string status = "pending", bool registered = false)
    {
        var invitationId = Guid.CreateVersion7();
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO organization.invitations
                (id, org_id, email, role, salt, key_version, code_version, code_hash, status, created_by, created_at, expires_at, registered_at)
            VALUES ($1, $2, 'kim@example.test', 'member', decode(repeat('00', 16), 'hex'), 1, $3, $4, $6, $5, now(), now() + interval '7 days',
                    CASE WHEN $7 THEN now() END)
            """,
            connection);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = invitationId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organizationId });
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = codeVersion });
        command.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = codeHash });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = createdBy });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = status });
        command.Parameters.Add(new NpgsqlParameter<bool> { TypedValue = registered });
        await command.ExecuteNonQueryAsync(CancellationToken);
        return invitationId;
    }

    // The connector's pending upstream messages about the invitation (the outbox is shared by every test cell).
    private async Task<List<(Guid MessageId, ConnectRequest Message)>> UpstreamAsync(Guid invitationId)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT message_id, payload FROM control_plane_connector.outbox WHERE type IN ('InvitationRegistered', 'InvitationRevoked') AND payload IS NOT NULL",
            connection);
        await using var reader = await command.ExecuteReaderAsync(CancellationToken);
        var messages = new List<(Guid, ConnectRequest)>();
        while (await reader.ReadAsync(CancellationToken))
        {
            var message = ConnectRequest.Parser.ParseFrom(reader.GetFieldValue<byte[]>(1));
            var about = message.PayloadCase == ConnectRequest.PayloadOneofCase.InvitationRegistered
                ? message.InvitationRegistered.InvitationId
                : message.InvitationRevoked.InvitationId;
            if (about == invitationId.ToString("D"))
            {
                messages.Add((reader.GetGuid(0), message));
            }
        }

        return messages;
    }

    private async Task<long> ReadyEventsAsync(Guid organizationId) =>
        await ScalarAsync<long>("SELECT count(*) FROM organization.outbox WHERE org_id = $1 AND event_type = 'organization.invitation-ready.v1'", organizationId);

    private async Task<string?> UpstreamStateAsync(Guid messageId) =>
        await ScalarAsync<string>(
            "SELECT CASE WHEN acked_at IS NOT NULL THEN 'acked' WHEN rejected_code IS NOT NULL THEN rejected_code ELSE 'pending' END FROM control_plane_connector.outbox WHERE message_id = $1",
            messageId);

    private async Task<int> AttemptsAsync(Guid messageId) =>
        await ScalarAsync<int>("SELECT attempts FROM control_plane_connector.outbox WHERE message_id = $1", messageId);

    private async Task<bool> HasPayloadAsync(Guid messageId) =>
        await ScalarAsync<bool>("SELECT payload IS NOT NULL FROM control_plane_connector.outbox WHERE message_id = $1", messageId);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100", Justification = "Test queries are constants; values are parameters.")]
    private async Task<T?> ScalarAsync<T>(string sql, Guid value)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = value });
        return await command.ExecuteScalarAsync(CancellationToken) is T result ? result : default;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        for (var attempt = 0; attempt < 150 && !await condition(); attempt++)
        {
            await Task.Delay(100, CancellationToken);
        }

        (await condition()).Should().BeTrue();
    }

    private sealed class MapScope(ConcurrentDictionary<Guid, string> organizations) : ICellOrganizationScope
    {
        public Task<bool> AreInCellAsync(IReadOnlyCollection<Guid> organizationIds, string cellId, CancellationToken cancellationToken) =>
            Task.FromResult(organizationIds.All(id => organizations.TryGetValue(id, out var mapped) && mapped == cellId));
    }

    private sealed class RegistrationRecorder(ConcurrentQueue<InvitationRegistered> registered) : ICellMessageHandler<InvitationRegistered>
    {
        public Task HandleAsync(CellMessage<InvitationRegistered> message, CancellationToken cancellationToken)
        {
            registered.Enqueue(message.Payload);
            return Task.CompletedTask;
        }
    }

    // A lock timeout or an unreachable database while marking the invitation.
    private sealed class FailingRegistration(FailureLog log, IOrganizationContext context) : IInvitationRegistration
    {
        public Task<bool> MarkRegisteredAsync(Guid invitationId, int codeVersion, CancellationToken cancellationToken)
        {
            log.Organizations.Enqueue(context.OrganizationId);
            throw new TimeoutException("The organization lock was not granted in time.");
        }
    }

    private sealed class FailureLog
    {
        public ConcurrentQueue<Guid?> Organizations { get; } = new();

        public int Calls => Organizations.Count;
    }
}
