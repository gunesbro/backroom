using System.Collections.Concurrent;
using AwesomeAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Backroom.ControlPlane.Contracts.V1;
using Backroom.Modules.CellManagement.Contracts;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;

namespace Backroom.Modules.CellManagement.IntegrationTests;

/// <summary>
/// Upstream delivery over the link (docs/contracts/phase-1.md, "Delivery" and "Scope of upstream
/// messages"): handled once, only for organizations mapped to the sending cell.
/// </summary>
[Trait("Category", "Privacy")]
public sealed class CellLinkUpstreamTests(PostgresDatabase database, KeycloakRealm keycloak) : IAsyncLifetime
{
    private readonly FakeScope _scope = new();
    private readonly RecordingHandler _handler = new();
    private readonly CountingHandler<MembershipDigest> _digests = new();
    private readonly CountingHandler<MembershipSnapshot> _snapshots = new();
    private readonly List<GrpcChannel> _channels = [];
    private WebApplication _controlPlane = null!;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() =>
        _controlPlane = await ControlPlaneUnderTest.StartAsync(database, keycloak, configureServices: services =>
        {
            services.AddSingleton<ICellOrganizationScope>(_scope);
            services.AddSingleton<ICellMessageHandler<MembershipChanged>>(_handler);
            services.AddSingleton<ICellMessageHandler<MembershipDigest>>(_digests);
            services.AddSingleton<ICellMessageHandler<MembershipSnapshot>>(_snapshots);
        });

    public async ValueTask DisposeAsync()
    {
        _channels.ForEach(channel => channel.Dispose());
        await _controlPlane.DisposeAsync();
    }

    [Fact]
    public async Task Message_ForAnOrganizationOfTheCell_IsHandledOnceAndAcknowledgedAsync()
    {
        await using var cell = await TestCell.RegisterAsync(_controlPlane, keycloak, CancellationToken);
        var organizationId = _scope.Map(cell.CellId);
        var messageId = Guid.CreateVersion7();
        using var call = await ConnectAsync(cell);

        var first = await SendAsync(call, MembershipChangedRequest(messageId, organizationId));
        var duplicate = await SendAsync(call, MembershipChangedRequest(messageId, organizationId));

        first.Ack.MessageId.Should().Be(messageId.ToString("D"));
        duplicate.Ack.MessageId.Should().Be(messageId.ToString("D"));
        _handler.Handled.Should().ContainSingle().Which.Should().Be((messageId, cell.CellId, organizationId.ToString("D")));
    }

    [Fact]
    public async Task Message_ForAnOrganizationOfAnotherCell_IsRejectedForGoodAndNotHandledAsync()
    {
        await using var cell = await TestCell.RegisterAsync(_controlPlane, keycloak, CancellationToken);
        var otherCellsOrganization = _scope.Map("another-cell");
        var messageId = Guid.CreateVersion7();
        using var call = await ConnectAsync(cell);

        var response = await SendAsync(call, MembershipChangedRequest(messageId, otherCellsOrganization));

        response.Nack.Should().BeEquivalentTo(new Nack { MessageId = messageId.ToString("D"), Code = "directory.organization_not_in_cell", Retryable = false });
        _handler.Handled.Should().BeEmpty();
    }

    [Fact]
    public async Task Message_RejectedByItsHandler_GetsANonRetryableNackWithTheCodeAsync()
    {
        await using var cell = await TestCell.RegisterAsync(_controlPlane, keycloak, CancellationToken);
        var organizationId = _scope.Map(cell.CellId);
        _handler.Failure = new CellMessageRejectedException("organization.member_unknown");
        using var call = await ConnectAsync(cell);

        var response = await SendAsync(call, MembershipChangedRequest(Guid.CreateVersion7(), organizationId));

        response.Nack.Code.Should().Be("organization.member_unknown");
        response.Nack.Retryable.Should().BeFalse();
    }

    [Fact]
    public async Task Message_WhoseHandlerFails_GetsARetryableNackAndIsHandledWhenSentAgainAsync()
    {
        await using var cell = await TestCell.RegisterAsync(_controlPlane, keycloak, CancellationToken);
        var organizationId = _scope.Map(cell.CellId);
        var messageId = Guid.CreateVersion7();
        _handler.Failure = new InvalidOperationException("The handler failed.");
        using var call = await ConnectAsync(cell);

        var failed = await SendAsync(call, MembershipChangedRequest(messageId, organizationId));
        _handler.Failure = null;
        var retried = await SendAsync(call, MembershipChangedRequest(messageId, organizationId));

        failed.Nack.Should().BeEquivalentTo(new Nack { MessageId = messageId.ToString("D"), Code = "processing_failed", Retryable = true });
        retried.Ack.MessageId.Should().Be(messageId.ToString("D"));
        _handler.Handled.Should().ContainSingle();
    }

    [Fact]
    public async Task Message_OfATypeWithoutAHandler_GetsARetryableNackAsync()
    {
        await using var cell = await TestCell.RegisterAsync(_controlPlane, keycloak, CancellationToken);
        var organizationId = _scope.Map(cell.CellId);
        using var call = await ConnectAsync(cell);

        var response = await SendAsync(call, new ConnectRequest
        {
            Envelope = new Envelope { MessageId = Guid.CreateVersion7().ToString("D") },
            OrganizationUpdated = new OrganizationUpdated { OrganizationId = organizationId.ToString("D"), Name = "Renamed", Version = 2 },
        });

        response.Nack.Code.Should().Be("link.not_ready");
        response.Nack.Retryable.Should().BeTrue();
    }

    [Theory]
    [InlineData("not-a-guid", "0190a000-0000-7000-8000-000000000001", "invalid_message_id")]
    [InlineData("0190a000-0000-7000-8000-000000000002", "not-a-guid", "invalid_organization_id")]
    public async Task Message_WithAnInvalidId_IsRejectedForGoodAsync(string messageId, string organizationId, string code)
    {
        await using var cell = await TestCell.RegisterAsync(_controlPlane, keycloak, CancellationToken);
        using var call = await ConnectAsync(cell);

        var response = await SendAsync(call, new ConnectRequest
        {
            Envelope = new Envelope { MessageId = messageId },
            MembershipChanged = new MembershipChanged { OrganizationId = organizationId, AccountId = Guid.NewGuid().ToString("D"), Version = 1 },
        });

        response.Nack.Code.Should().Be(code);
        response.Nack.Retryable.Should().BeFalse();
        _handler.Handled.Should().BeEmpty();
    }

    [Fact]
    public async Task Digest_NamingAnOrganizationOfAnotherCell_IsRejectedAsync()
    {
        await using var cell = await TestCell.RegisterAsync(_controlPlane, keycloak, CancellationToken);
        var response = await SendDigestAsync(cell, [_scope.Map(cell.CellId), _scope.Map("another-cell")]);

        response.Nack.Code.Should().Be("directory.organization_not_in_cell");
        response.Nack.Retryable.Should().BeFalse();
        _digests.Count.Should().Be(0);
    }

    [Fact]
    public async Task Snapshot_WithAMemberOfAnotherCellsOrganization_IsRejectedAsync()
    {
        await using var cell = await TestCell.RegisterAsync(_controlPlane, keycloak, CancellationToken);
        var own = _scope.Map(cell.CellId);
        var foreign = _scope.Map("another-cell");
        using var call = await ConnectAsync(cell);

        var response = await SendAsync(call, new ConnectRequest
        {
            Envelope = new Envelope { MessageId = Guid.CreateVersion7().ToString("D") },
            MembershipSnapshot = new MembershipSnapshot
            {
                OrganizationId = own.ToString("D"),
                Members = { new MembershipChanged { OrganizationId = foreign.ToString("D"), AccountId = Guid.NewGuid().ToString("D"), Version = 1 } },
            },
        });

        response.Nack.Code.Should().Be("directory.organization_not_in_cell");
        _snapshots.Count.Should().Be(0);
    }

    [Fact]
    public async Task Digest_NamingAnOrganizationTwice_IsHandledAsync()
    {
        await using var cell = await TestCell.RegisterAsync(_controlPlane, keycloak, CancellationToken);
        var own = _scope.Map(cell.CellId);

        var response = await SendDigestAsync(cell, [own, own]);

        response.PayloadCase.Should().Be(ConnectResponse.PayloadOneofCase.Ack);
        _digests.Count.Should().Be(1);
    }

    [Fact]
    public async Task Digest_WithAnEmptyOrganizationId_IsInvalidAsync()
    {
        await using var cell = await TestCell.RegisterAsync(_controlPlane, keycloak, CancellationToken);
        using var call = await ConnectAsync(cell);

        var response = await SendAsync(call, new ConnectRequest
        {
            Envelope = new Envelope { MessageId = Guid.CreateVersion7().ToString("D") },
            MembershipDigest = new MembershipDigest { Organizations = { new OrganizationMembershipDigest { OrganizationId = string.Empty } } },
        });

        response.Nack.Code.Should().Be("invalid_organization_id");
    }

    [Fact]
    public async Task Digest_WithTooManyOrganizations_IsRefusedWithItsOwnCodeAsync()
    {
        await using var cell = await TestCell.RegisterAsync(_controlPlane, keycloak, CancellationToken);

        var response = await SendDigestAsync(cell, [.. Enumerable.Range(0, ICellOrganizationScope.MaxOrganizationsPerCheck + 1).Select(_ => Guid.CreateVersion7())]);

        response.Nack.Code.Should().Be("too_many_organizations");
        response.Nack.Retryable.Should().BeFalse();
    }

    [Fact]
    public async Task Message_WhenTheScopeCheckFails_GetsARetryableNackAndKeepsTheStreamAsync()
    {
        // Regression: a failing scope query ended the cell's whole stream.
        await using var cell = await TestCell.RegisterAsync(_controlPlane, keycloak, CancellationToken);
        var organizationId = _scope.Map(cell.CellId);
        _scope.Fail = true;
        using var call = await ConnectAsync(cell);

        var failed = await SendAsync(call, MembershipChangedRequest(Guid.CreateVersion7(), organizationId));
        _scope.Fail = false;
        var next = await SendAsync(call, MembershipChangedRequest(Guid.CreateVersion7(), organizationId));

        failed.Nack.Should().BeEquivalentTo(new Nack { MessageId = failed.Nack.MessageId, Code = "processing_failed", Retryable = true });
        next.PayloadCase.Should().Be(ConnectResponse.PayloadOneofCase.Ack);
    }

    [Fact]
    public async Task MessageId_UsedByTwoCells_IsHandledForEachAsync()
    {
        // Regression: de-duplication by message id alone let one cell suppress another cell's message.
        await using var first = await TestCell.RegisterAsync(_controlPlane, keycloak, CancellationToken);
        await using var second = await TestCell.RegisterAsync(_controlPlane, keycloak, CancellationToken);
        var messageId = Guid.CreateVersion7();
        using var firstCall = await ConnectAsync(first);
        using var secondCall = await ConnectAsync(second);

        var fromFirst = await SendAsync(firstCall, MembershipChangedRequest(messageId, _scope.Map(first.CellId)));
        var fromSecond = await SendAsync(secondCall, MembershipChangedRequest(messageId, _scope.Map(second.CellId)));

        fromFirst.PayloadCase.Should().Be(ConnectResponse.PayloadOneofCase.Ack);
        fromSecond.PayloadCase.Should().Be(ConnectResponse.PayloadOneofCase.Ack);
        _handler.Handled.Select(handled => handled.CellId).Should().BeEquivalentTo([first.CellId, second.CellId]);
    }

    [Fact]
    public async Task RefusedMessages_MoreThanTheLimitInAMinute_CloseTheStreamAsync()
    {
        await using var cell = await TestCell.RegisterAsync(_controlPlane, keycloak, CancellationToken);
        using var call = await ConnectAsync(cell);
        var invalid = new ConnectRequest
        {
            Envelope = new Envelope { MessageId = Guid.CreateVersion7().ToString("D") },
            MembershipChanged = new MembershipChanged { OrganizationId = "not-a-guid", AccountId = Guid.NewGuid().ToString("D"), Version = 1 },
        };

        for (var i = 0; i < 300; i++)
        {
            (await SendAsync(call, invalid)).Nack.Code.Should().Be("invalid_organization_id");
        }

        await call.RequestStream.WriteAsync(invalid, CancellationToken);
        var read = () => call.ResponseStream.MoveNext(CancellationToken);

        (await read.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.ResourceExhausted);
    }

    private async Task<ConnectResponse> SendDigestAsync(TestCell cell, IEnumerable<Guid> organizations)
    {
        using var call = await ConnectAsync(cell);
        var digest = new MembershipDigest();
        digest.Organizations.AddRange(organizations.Select(id => new OrganizationMembershipDigest { OrganizationId = id.ToString("D") }));
        return await SendAsync(call, new ConnectRequest { Envelope = new Envelope { MessageId = Guid.CreateVersion7().ToString("D") }, MembershipDigest = digest });
    }

    private static ConnectRequest MembershipChangedRequest(Guid messageId, Guid organizationId) => new()
    {
        Envelope = new Envelope { MessageId = messageId.ToString("D") },
        MembershipChanged = new MembershipChanged
        {
            OrganizationId = organizationId.ToString("D"),
            AccountId = Guid.NewGuid().ToString("D"),
            Status = MembershipStatus.Active,
            Version = 1,
        },
    };

    private static async Task<ConnectResponse> SendAsync(AsyncDuplexStreamingCall<ConnectRequest, ConnectResponse> call, ConnectRequest request)
    {
        await call.RequestStream.WriteAsync(request, CancellationToken);
        (await call.ResponseStream.MoveNext(CancellationToken)).Should().BeTrue();
        return call.ResponseStream.Current;
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
        return call;
    }

    private sealed class FakeScope : ICellOrganizationScope
    {
        private readonly ConcurrentDictionary<Guid, string> _cells = new();

        public Guid Map(string cellId)
        {
            var organizationId = Guid.CreateVersion7();
            _cells[organizationId] = cellId;
            return organizationId;
        }

        public bool Fail { get; set; }

        public Task<bool> AreInCellAsync(IReadOnlyCollection<Guid> organizationIds, string cellId, CancellationToken cancellationToken) =>
            Fail
                ? throw new TimeoutException("The scope query timed out.")
                : Task.FromResult(organizationIds.All(id => _cells.TryGetValue(id, out var mapped) && mapped == cellId));
    }

    private sealed class RecordingHandler : ICellMessageHandler<MembershipChanged>
    {
        public ConcurrentQueue<(Guid MessageId, string CellId, string OrganizationId)> Handled { get; } = new();

        public Exception? Failure { get; set; }

        public Task HandleAsync(CellMessage<MembershipChanged> message, CancellationToken cancellationToken)
        {
            if (Failure is not null)
            {
                throw Failure;
            }

            Handled.Enqueue((message.MessageId, message.CellId, message.Payload.OrganizationId));
            return Task.CompletedTask;
        }
    }

    private sealed class CountingHandler<T> : ICellMessageHandler<T>
        where T : Google.Protobuf.IMessage
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public Task HandleAsync(CellMessage<T> message, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);
            return Task.CompletedTask;
        }
    }
}
