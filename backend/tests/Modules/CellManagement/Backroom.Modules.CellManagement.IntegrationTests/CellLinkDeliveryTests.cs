using System.Collections.Concurrent;
using AwesomeAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Backroom.BuildingBlocks.Application.Persistence;
using Backroom.ControlPlane.Contracts.V1;
using Backroom.Modules.CellManagement.Contracts;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;

namespace Backroom.Modules.CellManagement.IntegrationTests;

/// <summary>
/// Downstream delivery over the link (docs/contracts/phase-1.md, "Delivery"): at least once, in order,
/// settled by the cell's answer, only by the cell it was sent to.
/// </summary>
[Trait("Category", "Privacy")]
public sealed class CellLinkDeliveryTests(PostgresDatabase database, KeycloakRealm keycloak) : IAsyncLifetime
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly RecordingOutcomes _outcomes = new();
    private readonly List<GrpcChannel> _channels = [];
    private WebApplication _controlPlane = null!;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() =>
        _controlPlane = await ControlPlaneUnderTest.StartAsync(database, keycloak, _time, services =>
            services.AddSingleton<ICellMessageOutcomeHandler<OrganizationProvisioned>>(_outcomes));

    public async ValueTask DisposeAsync()
    {
        _channels.ForEach(channel => channel.Dispose());
        await _controlPlane.DisposeAsync();
    }

    [Fact]
    public async Task Connect_WithPendingMessages_SendsThemInOrderAfterWelcomeAsync()
    {
        await using var cell = await TestCell.RegisterAsync(_controlPlane, keycloak, CancellationToken);
        var first = await EnqueueAsync(cell.CellId, "first");
        var second = await EnqueueAsync(cell.CellId, "second");

        using var call = await ConnectAsync(cell);
        var received = await ReceiveAsync(call, 2);

        received.Select(message => message.Envelope.MessageId).Should().Equal(first.ToString("D"), second.ToString("D"));
        received.Select(message => message.OrganizationProvisioned.Name).Should().Equal("first", "second");
        received[1].Envelope.Sequence.Should().BeGreaterThan(received[0].Envelope.Sequence);
    }

    [Fact]
    public async Task Ack_SettlesTheMessageCallsItsHandlerAndDeletesThePayloadAsync()
    {
        await using var cell = await TestCell.RegisterAsync(_controlPlane, keycloak, CancellationToken);
        var messageId = await EnqueueAsync(cell.CellId, "acked");
        using var call = await ConnectAsync(cell);
        await ReceiveAsync(call, 1);

        await call.RequestStream.WriteAsync(new ConnectRequest { Ack = new Ack { MessageId = messageId.ToString("D") } }, CancellationToken);

        await WaitUntilAsync(async () => (await StateAsync(messageId)).Acked);
        (await StateAsync(messageId)).Should().Be(new MessageState(Acked: true, RejectedCode: null, HasPayload: false, Attempts: 0));
        _outcomes.Calls.Should().ContainSingle().Which.Should().Be((messageId, cell.CellId, (string?)null));
    }

    [Fact]
    public async Task NonRetryableNack_RejectsTheMessageAndCallsItsHandlerAsync()
    {
        await using var cell = await TestCell.RegisterAsync(_controlPlane, keycloak, CancellationToken);
        var messageId = await EnqueueAsync(cell.CellId, "refused");
        using var call = await ConnectAsync(cell);
        await ReceiveAsync(call, 1);

        await call.RequestStream.WriteAsync(
            new ConnectRequest { Nack = new Nack { MessageId = messageId.ToString("D"), Code = "organization.exists", Retryable = false } },
            CancellationToken);

        await WaitUntilAsync(async () => (await StateAsync(messageId)).RejectedCode is not null);
        (await StateAsync(messageId)).Should().Be(new MessageState(Acked: false, RejectedCode: "organization.exists", HasPayload: false, Attempts: 0));
        _outcomes.Calls.Should().ContainSingle().Which.Should().Be((messageId, cell.CellId, "organization.exists"));
    }

    [Fact]
    public async Task RetryableNack_SendsTheMessageAgainAfterABackoffAsync()
    {
        await using var cell = await TestCell.RegisterAsync(_controlPlane, keycloak, CancellationToken);
        var messageId = await EnqueueAsync(cell.CellId, "retried");
        using var call = await ConnectAsync(cell);
        await ReceiveAsync(call, 1);

        await call.RequestStream.WriteAsync(
            new ConnectRequest { Nack = new Nack { MessageId = messageId.ToString("D"), Code = "link.not_ready", Retryable = true } },
            CancellationToken);
        await WaitUntilAsync(async () => (await StateAsync(messageId)).Attempts == 1);
        var again = call.ResponseStream.MoveNext(CancellationToken);
        for (var step = 0; step < 30 && !again.IsCompleted; step++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            await Task.WhenAny(again, Task.Delay(100, CancellationToken));
        }

        (await again).Should().BeTrue();
        call.ResponseStream.Current.Envelope.MessageId.Should().Be(messageId.ToString("D"));
    }

    [Fact]
    public async Task UnansweredMessage_IsSentAgainAfterAReconnectAsync()
    {
        await using var cell = await TestCell.RegisterAsync(_controlPlane, keycloak, CancellationToken);
        var messageId = await EnqueueAsync(cell.CellId, "unanswered");
        using (var dropped = await ConnectAsync(cell))
        {
            await ReceiveAsync(dropped, 1);
        }

        using var call = await ConnectAsync(cell);
        var received = await ReceiveAsync(call, 1);

        received.Should().ContainSingle().Which.Envelope.MessageId.Should().Be(messageId.ToString("D"));
    }

    [Fact]
    public async Task Ack_FromAnotherCell_DoesNotSettleTheMessageAsync()
    {
        await using var cell = await TestCell.RegisterAsync(_controlPlane, keycloak, CancellationToken);
        await using var other = await TestCell.RegisterAsync(_controlPlane, keycloak, CancellationToken);
        var messageId = await EnqueueAsync(cell.CellId, "not yours");
        using var call = await ConnectAsync(other);

        await call.RequestStream.WriteAsync(new ConnectRequest { Ack = new Ack { MessageId = messageId.ToString("D") } }, CancellationToken);
        await call.RequestStream.WriteAsync(
            new ConnectRequest { Envelope = new Envelope { MessageId = "probe" }, MembershipDigest = new MembershipDigest() }, CancellationToken);
        await call.ResponseStream.MoveNext(CancellationToken);

        (await StateAsync(messageId)).Should().Be(new MessageState(Acked: false, RejectedCode: null, HasPayload: true, Attempts: 0));
        _outcomes.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Ack_WhenTheHandlerFails_KeepsTheMessagePendingForARetryAsync()
    {
        await using var cell = await TestCell.RegisterAsync(_controlPlane, keycloak, CancellationToken);
        var messageId = await EnqueueAsync(cell.CellId, "failing");
        _outcomes.Failure = new InvalidOperationException("The handler failed.");
        using var call = await ConnectAsync(cell);
        await ReceiveAsync(call, 1);

        await call.RequestStream.WriteAsync(new ConnectRequest { Ack = new Ack { MessageId = messageId.ToString("D") } }, CancellationToken);

        await WaitUntilAsync(async () => (await StateAsync(messageId)).Attempts == 1);
        (await StateAsync(messageId)).Should().Be(new MessageState(Acked: false, RejectedCode: null, HasPayload: true, Attempts: 1));
    }

    [Fact]
    public async Task Ack_WhenTheHandlerTimesOut_KeepsTheStreamAndTheMessagePendingAsync()
    {
        // Regression: a TaskCanceledException from a handler ended the cell's whole stream.
        await using var cell = await TestCell.RegisterAsync(_controlPlane, keycloak, CancellationToken);
        var messageId = await EnqueueAsync(cell.CellId, "timing out");
        _outcomes.Failure = new TaskCanceledException("A call inside the handler timed out.");
        using var call = await ConnectAsync(cell);
        await ReceiveAsync(call, 1);

        await call.RequestStream.WriteAsync(new ConnectRequest { Ack = new Ack { MessageId = messageId.ToString("D") } }, CancellationToken);
        await WaitUntilAsync(async () => (await StateAsync(messageId)).Attempts == 1);
        await call.RequestStream.WriteAsync(
            new ConnectRequest { Envelope = new Envelope { MessageId = "probe" }, MembershipDigest = new MembershipDigest() }, CancellationToken);

        (await call.ResponseStream.MoveNext(CancellationToken)).Should().BeTrue("the stream is still open");
        (await StateAsync(messageId)).Should().Be(new MessageState(Acked: false, RejectedCode: null, HasPayload: true, Attempts: 1));
    }

    [Fact]
    public async Task RetryableNack_AfterTheLastAttempt_RejectsTheMessageAsExpiredAsync()
    {
        await using var cell = await TestCell.RegisterAsync(_controlPlane, keycloak, CancellationToken);
        var messageId = await EnqueueAsync(cell.CellId, "expiring");
        await ExecuteAsync("UPDATE cell_management.outbound_messages SET attempts = 49 WHERE message_id = $1", messageId);
        using var call = await ConnectAsync(cell);
        await ReceiveAsync(call, 1);

        await call.RequestStream.WriteAsync(
            new ConnectRequest { Nack = new Nack { MessageId = messageId.ToString("D"), Code = "link.not_ready", Retryable = true } },
            CancellationToken);

        await WaitUntilAsync(async () => (await StateAsync(messageId)).RejectedCode is not null);
        (await StateAsync(messageId)).Should().Be(new MessageState(Acked: false, RejectedCode: "delivery_expired", HasPayload: false, Attempts: 50));
        _outcomes.Calls.Should().ContainSingle().Which.Should().Be((messageId, cell.CellId, "delivery_expired"));
    }

    [Fact]
    public async Task UnansweredMessage_IsSentAgainOnTheSameStreamWhenItsLeaseEndsAsync()
    {
        await using var cell = await TestCell.RegisterAsync(_controlPlane, keycloak, CancellationToken);
        var messageId = await EnqueueAsync(cell.CellId, "lease");
        using var call = await ConnectAsync(cell);
        await ReceiveAsync(call, 1);

        var again = call.ResponseStream.MoveNext(CancellationToken);
        for (var step = 0; step < 40 && !again.IsCompleted; step++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            await Task.WhenAny(again, Task.Delay(100, CancellationToken));
        }

        (await again).Should().BeTrue();
        call.ResponseStream.Current.Envelope.MessageId.Should().Be(messageId.ToString("D"));
    }

    [Fact]
    public async Task UnansweredMessage_AfterTheLastAttempt_ExpiresAsync()
    {
        // Regression: an unanswered message was leased again forever without counting attempts.
        await using var cell = await TestCell.RegisterAsync(_controlPlane, keycloak, CancellationToken);
        var messageId = await EnqueueAsync(cell.CellId, "never answered");
        await ExecuteAsync("UPDATE cell_management.outbound_messages SET attempts = 49 WHERE message_id = $1", messageId);
        using var call = await ConnectAsync(cell);
        await ReceiveAsync(call, 1);

        for (var step = 0; step < 40 && (await StateAsync(messageId)).RejectedCode is null; step++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(100, CancellationToken);
        }

        (await StateAsync(messageId)).Should().Be(new MessageState(Acked: false, RejectedCode: "delivery_expired", HasPayload: false, Attempts: 50));
        _outcomes.Calls.Should().ContainSingle().Which.Should().Be((messageId, cell.CellId, "delivery_expired"));
    }

    [Fact]
    public async Task Ack_WhenTheHandlerFailsAtTheLastAttempt_ClosesItWithoutReportingARejectionAsync()
    {
        // Regression: a message the cell had acknowledged could expire and be reported as rejected.
        // Now it is closed as handler_failed (payload deleted, operations alert) and no handler is told.
        await using var cell = await TestCell.RegisterAsync(_controlPlane, keycloak, CancellationToken);
        var messageId = await EnqueueAsync(cell.CellId, "acknowledged but failing");
        await ExecuteAsync("UPDATE cell_management.outbound_messages SET attempts = 49 WHERE message_id = $1", messageId);
        _outcomes.Failure = new InvalidOperationException("The handler failed.");
        using var call = await ConnectAsync(cell);
        await ReceiveAsync(call, 1);

        await call.RequestStream.WriteAsync(new ConnectRequest { Ack = new Ack { MessageId = messageId.ToString("D") } }, CancellationToken);

        await WaitUntilAsync(async () => (await StateAsync(messageId)).RejectedCode is not null);
        (await StateAsync(messageId)).Should().Be(new MessageState(Acked: false, RejectedCode: "handler_failed", HasPayload: false, Attempts: 50));
        _outcomes.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Ack_WhenTheHandlerFailsOnce_SettlesLaterWithoutSendingAgainAsync()
    {
        // Regression: a message the cell had acknowledged was sent to it again, and a later answer could
        // still report it as rejected. Now the cell's first answer is kept and the settlement is retried here.
        await using var cell = await TestCell.RegisterAsync(_controlPlane, keycloak, CancellationToken);
        var messageId = await EnqueueAsync(cell.CellId, "acknowledged once");
        _outcomes.Failure = new InvalidOperationException("The handler failed.");
        using var call = await ConnectAsync(cell);
        await ReceiveAsync(call, 1);
        await call.RequestStream.WriteAsync(new ConnectRequest { Ack = new Ack { MessageId = messageId.ToString("D") } }, CancellationToken);
        await WaitUntilAsync(async () => (await StateAsync(messageId)).Attempts == 1);
        (await CellAnswerAsync(messageId)).Should().Be(":acknowledged");
        _outcomes.Failure = null;

        var resent = call.ResponseStream.MoveNext(CancellationToken);
        for (var step = 0; step < 10 && !(await StateAsync(messageId)).Acked; step++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(100, CancellationToken);
        }

        (await StateAsync(messageId)).Should().Be(new MessageState(Acked: true, RejectedCode: null, HasPayload: false, Attempts: 1));
        _outcomes.Calls.Should().ContainSingle().Which.Should().Be((messageId, cell.CellId, (string?)null));
        resent.IsCompleted.Should().BeFalse("the acknowledged message is not sent to the cell again");
    }

    [Fact]
    public async Task NonRetryableNack_WithAReservedCode_IsStoredAsInvalidAsync()
    {
        await using var cell = await TestCell.RegisterAsync(_controlPlane, keycloak, CancellationToken);
        var messageId = await EnqueueAsync(cell.CellId, "reserved code");
        using var call = await ConnectAsync(cell);
        await ReceiveAsync(call, 1);

        await call.RequestStream.WriteAsync(
            new ConnectRequest { Nack = new Nack { MessageId = messageId.ToString("D"), Code = "handler_failed", Retryable = false } },
            CancellationToken);

        await WaitUntilAsync(async () => (await StateAsync(messageId)).RejectedCode is not null);
        (await StateAsync(messageId)).RejectedCode.Should().Be("invalid_code");
    }

    [Fact]
    public async Task Enqueue_WhenTheUnitOfWorkRollsBack_StoresNothingAsync()
    {
        await using var cell = await TestCell.RegisterAsync(_controlPlane, keycloak, CancellationToken);

        var messageId = await EnqueueAsync(cell.CellId, "rolled back", commit: false);

        (await ExistsAsync(messageId)).Should().BeFalse();
    }

    [Fact]
    public async Task Enqueue_WithAControlMessage_IsRefusedAsync()
    {
        await using var cell = await TestCell.RegisterAsync(_controlPlane, keycloak, CancellationToken);
        await using var scope = _controlPlane.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await unitOfWork.BeginAsync(CancellationToken);

        var enqueue = () => scope.ServiceProvider.GetRequiredService<ICellLinkOutbox>()
            .EnqueueAsync(cell.CellId, new ConnectResponse { Welcome = new Welcome() }, CancellationToken);

        await enqueue.Should().ThrowAsync<ArgumentException>();
    }

    private async Task<Guid> EnqueueAsync(string cellId, string name, bool commit = true)
    {
        await using var scope = _controlPlane.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await unitOfWork.BeginAsync(CancellationToken);
        var messageId = await scope.ServiceProvider.GetRequiredService<ICellLinkOutbox>().EnqueueAsync(
            cellId,
            new ConnectResponse { OrganizationProvisioned = new OrganizationProvisioned { OrganizationId = Guid.CreateVersion7().ToString("D"), Name = name } },
            CancellationToken);
        if (commit)
        {
            await unitOfWork.CommitAsync(CancellationToken);
        }
        else
        {
            await unitOfWork.RollbackAsync(CancellationToken);
        }

        return messageId;
    }

    private async Task<AsyncDuplexStreamingCall<ConnectRequest, ConnectResponse>> ConnectAsync(TestCell cell)
    {
        var server = _controlPlane.GetTestServer();
        var channel = GrpcChannel.ForAddress(server.BaseAddress, new GrpcChannelOptions { HttpHandler = server.CreateHandler() });
        _channels.Add(channel);
        var call = new CellLinkService.CellLinkServiceClient(channel).Connect(
            new Metadata { { "Authorization", "Bearer " + await cell.Credentials.GetAccessTokenAsync(CancellationToken) } },
            cancellationToken: CancellationToken);
        await call.RequestStream.WriteAsync(new ConnectRequest { Hello = new Hello { CellId = cell.CellId, Version = "1" } }, CancellationToken);
        await call.ResponseStream.MoveNext(CancellationToken);
        call.ResponseStream.Current.PayloadCase.Should().Be(ConnectResponse.PayloadOneofCase.Welcome);
        return call;
    }

    private static async Task<List<ConnectResponse>> ReceiveAsync(AsyncDuplexStreamingCall<ConnectRequest, ConnectResponse> call, int count)
    {
        var received = new List<ConnectResponse>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        while (received.Count < count && await call.ResponseStream.MoveNext(timeout.Token))
        {
            received.Add(call.ResponseStream.Current);
        }

        return received;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        for (var attempt = 0; attempt < 100 && !await condition(); attempt++)
        {
            await Task.Delay(100, CancellationToken);
        }

        (await condition()).Should().BeTrue();
    }

    private async Task<MessageState> StateAsync(Guid messageId)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT acked_at IS NOT NULL, rejected_code, payload IS NOT NULL, attempts FROM cell_management.outbound_messages WHERE message_id = $1",
            connection);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = messageId });
        await using var reader = await command.ExecuteReaderAsync(CancellationToken);
        await reader.ReadAsync(CancellationToken);
        return new MessageState(
            reader.GetBoolean(0), await reader.IsDBNullAsync(1, CancellationToken) ? null : reader.GetString(1), reader.GetBoolean(2), reader.GetInt32(3));
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100", Justification = "Test setup with constant SQL; values are parameters.")]
    private async Task ExecuteAsync(string sql, Guid messageId)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = messageId });
        await command.ExecuteNonQueryAsync(CancellationToken);
    }

    private async Task<string?> CellAnswerAsync(Guid messageId)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand("SELECT cell_answer FROM cell_management.outbound_messages WHERE message_id = $1", connection);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = messageId });
        return await command.ExecuteScalarAsync(CancellationToken) as string;
    }

    private async Task<bool> ExistsAsync(Guid messageId)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM cell_management.outbound_messages WHERE message_id = $1)", connection);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = messageId });
        return await command.ExecuteScalarAsync(CancellationToken) is true;
    }

    private sealed record MessageState(bool Acked, string? RejectedCode, bool HasPayload, int Attempts);

    private sealed class RecordingOutcomes : ICellMessageOutcomeHandler<OrganizationProvisioned>
    {
        public ConcurrentQueue<(Guid MessageId, string CellId, string? Code)> Calls { get; } = new();

        public Exception? Failure { get; set; }

        public Task OnAcknowledgedAsync(CellMessage<OrganizationProvisioned> message, CancellationToken cancellationToken) =>
            RecordAsync(message, null);

        public Task OnRejectedAsync(CellMessage<OrganizationProvisioned> message, string code, CancellationToken cancellationToken) =>
            RecordAsync(message, code);

        private Task RecordAsync(CellMessage<OrganizationProvisioned> message, string? code)
        {
            if (Failure is not null)
            {
                throw Failure;
            }

            Calls.Enqueue((message.MessageId, message.CellId, code));
            return Task.CompletedTask;
        }
    }
}
