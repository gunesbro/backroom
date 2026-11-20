using System.Collections.Concurrent;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Backroom.BuildingBlocks.Application.Organizations;
using Backroom.BuildingBlocks.Application.Persistence;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.ControlPlane.Contracts.V1;
using Backroom.Modules.CellManagement.Contracts;
using Backroom.Modules.ControlPlaneConnector.Infrastructure.Link;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;

namespace Backroom.Modules.CellManagement.IntegrationTests;

/// <summary>
/// Both ends of link delivery together (docs/contracts/phase-1.md, "Delivery"): the cell's worker sends its
/// outbox and handles downstream messages through its inbox; the control plane does the same the other way.
/// </summary>
[Trait("Category", "Privacy")]
[Collection(LinkWorkerCollection.Name)]
public sealed class CellLinkEndToEndTests(PostgresDatabase database, KeycloakRealm keycloak) : IAsyncLifetime
{
    private readonly ConcurrentDictionary<Guid, string> _organizations = new();
    private readonly ConcurrentQueue<string> _upstreamHandled = new();
    private readonly List<Guid> _poison = [];
    private WebApplication _controlPlane = null!;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() =>
        _controlPlane = await ControlPlaneUnderTest.StartAsync(database, keycloak, configureServices: services =>
        {
            services.AddSingleton<ICellOrganizationScope>(new MapScope(_organizations));
            services.AddSingleton<ICellMessageHandler<MembershipChanged>>(new UpstreamRecorder(_upstreamHandled));
        });

    public async ValueTask DisposeAsync()
    {
        // The cell outbox is shared by every test cell: a stream-breaking message left pending would break
        // the next test's streams.
        foreach (var messageId in _poison)
        {
            await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("DELETE FROM control_plane_connector.outbox WHERE message_id = $1", connection);
            command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = messageId });
            await command.ExecuteNonQueryAsync();
        }

        await _controlPlane.DisposeAsync();
    }

    [Fact]
    public async Task UpstreamMessage_IsDeliveredHandledAndSettledInTheCellsOutboxAsync()
    {
        await using var cell = await RegisterCellAsync();
        var organizationId = Guid.CreateVersion7();
        _organizations[organizationId] = cell.CellId;
        var messageId = await EnqueueUpstreamAsync(cell, organizationId);

        await StartAsync(cell);
        try
        {
            await WaitUntilAsync(async () => await CellOutboxStateAsync(messageId) == "acked");
            _upstreamHandled.Should().ContainSingle().Which.Should().Be(organizationId.ToString("D"));
            (await CellOutboxHasPayloadAsync(messageId)).Should().BeFalse("the payload is deleted once acknowledged");
        }
        finally
        {
            await cell.Link.StopAsync(CancellationToken);
        }
    }

    [Fact]
    public async Task UpstreamMessage_ForAnOrganizationOfAnotherCell_IsRejectedInTheCellsOutboxAsync()
    {
        await using var cell = await RegisterCellAsync();
        var foreign = Guid.CreateVersion7();
        _organizations[foreign] = "another-cell";
        var messageId = await EnqueueUpstreamAsync(cell, foreign);

        await StartAsync(cell);
        try
        {
            await WaitUntilAsync(async () => await CellOutboxStateAsync(messageId) == "directory.organization_not_in_cell");
            _upstreamHandled.Should().BeEmpty();
        }
        finally
        {
            await cell.Link.StopAsync(CancellationToken);
        }
    }

    [Fact]
    public async Task DownstreamMessage_IsHandledOnceInItsOrganizationAndAcknowledgedAsync()
    {
        var handled = new ConcurrentQueue<(Guid MessageId, Guid? Organization)>();
        await using var cell = await RegisterCellAsync(services =>
            services.AddScoped<IDownstreamHandler<OrganizationProvisioned>>(provider => new DownstreamRecorder(handled, provider.GetRequiredService<IOrganizationContext>())));
        var organizationId = Guid.CreateVersion7();
        var messageId = await EnqueueDownstreamAsync(cell.CellId, organizationId);

        await StartAsync(cell);
        try
        {
            await WaitUntilAsync(async () => await ControlPlaneOutboundStateAsync(messageId) == "acked");
            handled.Should().ContainSingle().Which.Should().Be((messageId, (Guid?)organizationId));
        }
        finally
        {
            await cell.Link.StopAsync(CancellationToken);
        }
    }

    [Fact]
    public async Task DownstreamMessage_WithoutAHandler_StaysPendingForARetryAsync()
    {
        await using var cell = await RegisterCellAsync();
        var messageId = await EnqueueDownstreamAsync(cell.CellId, Guid.CreateVersion7());

        await StartAsync(cell);
        try
        {
            await WaitUntilAsync(async () => await ControlPlaneOutboundAttemptsAsync(messageId) >= 1);
            (await ControlPlaneOutboundStateAsync(messageId)).Should().Be("pending");
        }
        finally
        {
            await cell.Link.StopAsync(CancellationToken);
        }
    }

    [Fact]
    public async Task DownstreamMessage_RejectedByItsHandler_IsRejectedOnTheControlPlaneAsync()
    {
        await using var cell = await RegisterCellAsync(services =>
            services.AddScoped<IDownstreamHandler<OrganizationProvisioned>>(_ => new RejectingHandler("organization.exists")));
        var messageId = await EnqueueDownstreamAsync(cell.CellId, Guid.CreateVersion7());

        await StartAsync(cell);
        try
        {
            await WaitUntilAsync(async () => await ControlPlaneOutboundStateAsync(messageId) == "organization.exists");
        }
        finally
        {
            await cell.Link.StopAsync(CancellationToken);
        }
    }

    [Fact]
    public async Task UpstreamMessage_ThatBreaksTheStream_DoesNotTakeTheMessagesBehindItAlongAsync()
    {
        // Regression: every message sent on a stream that broke was charged an attempt, so one message that
        // always breaks the stream would have expired the messages queued behind it too.
        await using var cell = await RegisterCellAsync();
        var organizationId = Guid.CreateVersion7();
        _organizations[organizationId] = cell.CellId;
        var poison = await InsertPoisonAsync();
        var first = await EnqueueUpstreamAsync(cell, organizationId);
        var second = await EnqueueUpstreamAsync(cell, organizationId);

        await cell.Link.StartAsync(CancellationToken);
        try
        {
            await WaitUntilAsync(async () => await CellOutboxStateAsync(first) == "acked" && await CellOutboxStateAsync(second) == "acked");
            (await CellOutboxAttemptsAsync(first)).Should().Be(0);
            (await CellOutboxAttemptsAsync(second)).Should().Be(0);
            (await CellOutboxAttemptsAsync(poison)).Should().BeGreaterThanOrEqualTo(1);
        }
        finally
        {
            await cell.Link.StopAsync(CancellationToken);
        }
    }

    [Fact]
    public async Task UpstreamMessage_ThatBreaksTheStreamBehindAnotherOne_IsChargedAloneAsync()
    {
        // The new stream sends the older message alone first; once it is answered the window opens and the
        // breaking message is the oldest unanswered one when the stream ends.
        await using var cell = await RegisterCellAsync();
        var organizationId = Guid.CreateVersion7();
        _organizations[organizationId] = cell.CellId;
        var before = await EnqueueUpstreamAsync(cell, organizationId);
        var poison = await InsertPoisonAsync();
        var after = await EnqueueUpstreamAsync(cell, organizationId);

        await cell.Link.StartAsync(CancellationToken);
        try
        {
            await WaitUntilAsync(async () => await CellOutboxStateAsync(before) == "acked" && await CellOutboxStateAsync(after) == "acked");
            (await CellOutboxAttemptsAsync(before)).Should().Be(0);
            (await CellOutboxAttemptsAsync(after)).Should().Be(0);
            (await CellOutboxAttemptsAsync(poison)).Should().BeGreaterThanOrEqualTo(1);
        }
        finally
        {
            await cell.Link.StopAsync(CancellationToken);
        }
    }

    // A Hello inside the stream: the control plane ends the stream with InvalidArgument every time.
    private async Task<Guid> InsertPoisonAsync()
    {
        var messageId = Guid.CreateVersion7();
        _poison.Add(messageId);
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand(
            "INSERT INTO control_plane_connector.outbox (message_id, type, payload, created_at, next_attempt_at) VALUES ($1, 'Hello', $2, now(), now())",
            connection);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = messageId });
        command.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = Google.Protobuf.MessageExtensions.ToByteArray(new ConnectRequest { Hello = new Hello { CellId = "x", Version = "1" } }) });
        await command.ExecuteNonQueryAsync(CancellationToken);
        return messageId;
    }

    private async Task<int> CellOutboxAttemptsAsync(Guid messageId) =>
        await ScalarAsync<int>("SELECT attempts FROM control_plane_connector.outbox WHERE message_id = $1", messageId);

    [Theory]
    [InlineData("control_plane_connector.outbox")]
    [InlineData("control_plane_connector.link_inbox")]
    [InlineData("control_plane_connector.inbox")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100", Justification = "The table name is a test constant.")]
    public async Task LinkTables_CannotBeReadByTheApplicationRoleAsync(string table)
    {
        // Regression: pending payloads (an invitee's email, organization names) were readable across
        // organizations by every host role.
        await using var connection = new NpgsqlConnection(database.ApplicationConnectionString());
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM {table}", connection);

        var read = () => command.ExecuteScalarAsync(CancellationToken);

        (await read.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    private Task<TestCell> RegisterCellAsync(Action<IServiceCollection>? configureServices = null) =>
        TestCell.RegisterAsync(_controlPlane, keycloak, CancellationToken, database, configureServices: configureServices);

    private static async Task StartAsync(TestCell cell)
    {
        await cell.Link.StartAsync(CancellationToken);
        await WaitUntilAsync(() => Task.FromResult(cell.Link.IsConnected));
    }

    private static async Task<Guid> EnqueueUpstreamAsync(TestCell cell, Guid organizationId)
    {
        await using var scope = cell.Services.CreateAsyncScope();
        var session = scope.ServiceProvider.GetRequiredService<DatabaseSession>();
        await session.BeginAsync(CancellationToken);
        var messageId = await scope.ServiceProvider.GetRequiredService<UpstreamOutbox>().EnqueueAsync(
            new ConnectRequest
            {
                MembershipChanged = new MembershipChanged
                {
                    OrganizationId = organizationId.ToString("D"),
                    AccountId = Guid.NewGuid().ToString("D"),
                    Status = MembershipStatus.Active,
                    Version = 1,
                },
            },
            CancellationToken);
        await session.CommitAsync(CancellationToken);
        return messageId;
    }

    private async Task<Guid> EnqueueDownstreamAsync(string cellId, Guid organizationId)
    {
        await using var scope = _controlPlane.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await unitOfWork.BeginAsync(CancellationToken);
        var messageId = await scope.ServiceProvider.GetRequiredService<ICellLinkOutbox>().EnqueueAsync(
            cellId,
            new ConnectResponse { OrganizationProvisioned = new OrganizationProvisioned { OrganizationId = organizationId.ToString("D"), Name = "Acme" } },
            CancellationToken);
        await unitOfWork.CommitAsync(CancellationToken);
        return messageId;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        for (var attempt = 0; attempt < 150 && !await condition(); attempt++)
        {
            await Task.Delay(100, CancellationToken);
        }

        (await condition()).Should().BeTrue();
    }

    private Task<string?> CellOutboxStateAsync(Guid messageId) => StateAsync("control_plane_connector.outbox", messageId);

    private Task<string?> ControlPlaneOutboundStateAsync(Guid messageId) => StateAsync("cell_management.outbound_messages", messageId);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100", Justification = "The table name is one of two constants; values are parameters.")]
    private async Task<string?> StateAsync(string table, Guid messageId) =>
        await ScalarAsync<string>(
            $"SELECT CASE WHEN acked_at IS NOT NULL THEN 'acked' WHEN rejected_code IS NOT NULL THEN rejected_code ELSE 'pending' END FROM {table} WHERE message_id = $1",
            messageId);

    private async Task<bool> CellOutboxHasPayloadAsync(Guid messageId) =>
        await ScalarAsync<bool>("SELECT payload IS NOT NULL FROM control_plane_connector.outbox WHERE message_id = $1", messageId);

    private async Task<int> ControlPlaneOutboundAttemptsAsync(Guid messageId) =>
        await ScalarAsync<int>("SELECT attempts FROM cell_management.outbound_messages WHERE message_id = $1", messageId);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100", Justification = "Test queries are constants; values are parameters.")]
    private async Task<T?> ScalarAsync<T>(string sql, Guid messageId)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = messageId });
        return await command.ExecuteScalarAsync(CancellationToken) is T value ? value : default;
    }

    private sealed class MapScope(ConcurrentDictionary<Guid, string> organizations) : ICellOrganizationScope
    {
        public Task<bool> AreInCellAsync(IReadOnlyCollection<Guid> organizationIds, string cellId, CancellationToken cancellationToken) =>
            Task.FromResult(organizationIds.All(id => organizations.TryGetValue(id, out var mapped) && mapped == cellId));
    }

    private sealed class UpstreamRecorder(ConcurrentQueue<string> handled) : ICellMessageHandler<MembershipChanged>
    {
        public Task HandleAsync(CellMessage<MembershipChanged> message, CancellationToken cancellationToken)
        {
            handled.Enqueue(message.Payload.OrganizationId);
            return Task.CompletedTask;
        }
    }

    private sealed class DownstreamRecorder(ConcurrentQueue<(Guid, Guid?)> handled, IOrganizationContext organization) : IDownstreamHandler<OrganizationProvisioned>
    {
        public Task HandleAsync(Guid messageId, OrganizationProvisioned message, CancellationToken cancellationToken)
        {
            handled.Enqueue((messageId, organization.OrganizationId));
            return Task.CompletedTask;
        }
    }

    private sealed class RejectingHandler(string code) : IDownstreamHandler<OrganizationProvisioned>
    {
        public Task HandleAsync(Guid messageId, OrganizationProvisioned message, CancellationToken cancellationToken) =>
            throw new LinkMessageRejectedException(code);
    }
}
