using AwesomeAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Backroom.BuildingBlocks.Application.Persistence;
using Backroom.ControlPlane.Contracts.V1;
using Backroom.Modules.CellManagement.Contracts;
using Backroom.Modules.Identity.Application;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;

namespace Backroom.Modules.CellManagement.IntegrationTests;

/// <summary>
/// Revoked sessions over the link (docs/contracts/phase-1.md, flow 5 and "Drift repair"): a <c>SessionRevoked</c> that Identity queued
/// reaches the cell's Realtime contract once and is acknowledged, a bad one is refused for good, a failing Valkey makes the cell ask
/// for it again, and <c>FullSync</c> carries the revocations of the cell's members (and no one else's).
/// </summary>
[Trait("Category", "Privacy")]
[Collection(LinkWorkerCollection.Name)]
public sealed class SessionRevocationLinkTests(PostgresDatabase database, KeycloakRealm keycloak) : IAsyncLifetime
{
    private WebApplication _controlPlane = null!;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _controlPlane = await ControlPlaneUnderTest.StartAsync(database, keycloak);

    public async ValueTask DisposeAsync() => await _controlPlane.DisposeAsync();

    [Fact]
    public async Task SessionRevoked_QueuedByIdentity_ReachesTheCellsRealtimeAndIsAcknowledgedAsync()
    {
        await using var cell = await RegisterCellAsync();
        var account = Guid.NewGuid();
        await AddMembershipAsync(account, cell.CellId);
        var told = await RevokeAsync(account, "sid-1");
        told.Should().Be(1);

        await StartAsync(cell);
        try
        {
            await WaitUntilAsync(async () => await PendingMessagesAsync(cell.CellId) == 0);
            cell.Revocations.Applied.Should().NotBeEmpty("the message, and FullSync after it, apply the same revocation; applying it again is harmless");
            cell.Revocations.Applied.Select(revocation => (revocation.AccountId, revocation.SessionId)).Distinct().Should().ContainSingle();
            var applied = cell.Revocations.Applied.First();
            applied.AccountId.Should().Be(account);
            applied.SessionId.Should().Be("sid-1");
            (applied.KeepUntil - applied.RevokedAt).Should().Be(TimeSpan.FromMinutes(5.5), "the cell keeps it for the longest token lifetime plus the skew");
        }
        finally
        {
            await cell.Link.StopAsync(CancellationToken);
        }
    }

    [Fact]
    public async Task SessionRevoked_ForEverySession_ArrivesWithoutASessionIdAsync()
    {
        await using var cell = await RegisterCellAsync();
        var account = Guid.NewGuid();
        await AddMembershipAsync(account, cell.CellId);
        await RevokeAsync(account, sessionId: null);

        await StartAsync(cell);
        try
        {
            await WaitUntilAsync(async () => await PendingMessagesAsync(cell.CellId) == 0);
            cell.Revocations.Applied.Should().Contain(revocation => revocation.AccountId == account && revocation.SessionId == null);
        }
        finally
        {
            await cell.Link.StopAsync(CancellationToken);
        }
    }

    [Fact]
    public async Task SessionRevoked_WithAnInvalidAccountOrDates_IsRefusedForGoodAsync()
    {
        await using var cell = await RegisterCellAsync();
        var invalidAccount = await EnqueueAsync(cell.CellId, new SessionRevoked
        {
            AccountId = "not-an-id",
            RevokedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            KeepUntil = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(5)),
        });
        var backwards = await EnqueueAsync(cell.CellId, new SessionRevoked
        {
            AccountId = Guid.NewGuid().ToString("D"),
            RevokedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            KeepUntil = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(-5)),
        });
        var missingDates = await EnqueueAsync(cell.CellId, new SessionRevoked { AccountId = Guid.NewGuid().ToString("D") });

        await StartAsync(cell);
        try
        {
            foreach (var message in new[] { invalidAccount, backwards, missingDates })
            {
                await WaitUntilAsync(async () => await StateAsync(message) == "realtime.session_revocation_invalid");
            }

            cell.Revocations.Applied.Should().BeEmpty();
        }
        finally
        {
            await cell.Link.StopAsync(CancellationToken);
        }
    }

    [Fact]
    public async Task SessionRevoked_WhenRealtimeCannotStoreIt_IsAnsweredRetryableAndAppliedLaterAsync()
    {
        await using var cell = await RegisterCellAsync();
        var account = Guid.NewGuid();
        await AddMembershipAsync(account, cell.CellId);
        cell.Revocations.Fail = true;
        await RevokeAsync(account, "sid-retry");

        await StartAsync(cell);
        try
        {
            await WaitUntilAsync(async () => await AttemptsAsync(cell.CellId) >= 1);
            (await PendingMessagesAsync(cell.CellId)).Should().Be(1, "the control plane keeps a message the cell could not apply");
            cell.Revocations.Applied.Should().BeEmpty();
        }
        finally
        {
            await cell.Link.StopAsync(CancellationToken);
        }
    }

    [Fact]
    public async Task FullSync_CarriesTheRevocationsOfTheCellsMembersOnlyAsync()
    {
        await using var cell = await RegisterCellAsync();
        var otherCell = await RegisterCellAsync();
        var member = Guid.NewGuid();
        var otherCellsMember = Guid.NewGuid();
        var expired = Guid.NewGuid();
        await AddMembershipAsync(member, cell.CellId);
        await AddMembershipAsync(otherCellsMember, otherCell.CellId);
        await AddMembershipAsync(expired, cell.CellId);
        var now = DateTimeOffset.UtcNow;

        // Rows without messages: only FullSync can bring them to the cell (a missed SessionRevoked).
        await AddRevokedSessionAsync(member, "sid-missed", now.AddMinutes(-1), now.AddMinutes(4));
        await AddRevokedSessionAsync(member, string.Empty, now.AddMinutes(-2), now.AddMinutes(3));
        await AddRevokedSessionAsync(otherCellsMember, "sid-elsewhere", now.AddMinutes(-1), now.AddMinutes(4));
        await AddRevokedSessionAsync(expired, "sid-old", now.AddMinutes(-10), now.AddMinutes(-1));

        await StartAsync(cell);
        try
        {
            await WaitUntilAsync(() => Task.FromResult(cell.Revocations.Applied.Count >= 2));
            cell.Revocations.Applied.Select(revocation => (revocation.AccountId, revocation.SessionId)).Should().BeEquivalentTo(
                [(member, (string?)"sid-missed"), (member, (string?)null)],
                "no other account's revocation and no expired one is sent to the cell");
        }
        finally
        {
            await cell.Link.StopAsync(CancellationToken);
        }
    }

    private Task<TestCell> RegisterCellAsync() => TestCell.RegisterAsync(_controlPlane, keycloak, CancellationToken, database);

    private static async Task StartAsync(TestCell cell)
    {
        await cell.Link.StartAsync(CancellationToken);
        await WaitUntilAsync(() => Task.FromResult(cell.Link.IsConnected));
    }

    private async Task<int> RevokeAsync(Guid account, string? sessionId)
    {
        await using var scope = _controlPlane.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SessionRevocations>().RevokeAsync(account, sessionId, CancellationToken);
    }

    private async Task<Guid> EnqueueAsync(string cellId, SessionRevoked message)
    {
        await using var scope = _controlPlane.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await unitOfWork.BeginAsync(CancellationToken);
        var id = await scope.ServiceProvider.GetRequiredService<ICellLinkOutbox>().EnqueueAsync(cellId, new ConnectResponse { SessionRevoked = message }, CancellationToken);
        await unitOfWork.CommitAsync(CancellationToken);
        return id;
    }

    private async Task AddMembershipAsync(Guid account, string cellId)
    {
        var organization = Guid.CreateVersion7();
        await ExecuteAsync(
            "INSERT INTO directory.organizations (id, name, region, cell_id, status, created_by, created_at) VALUES ($1, 'Acme', 'eu', $2, 'active', $3, now())",
            organization, cellId, account);
        await ExecuteAsync(
            "INSERT INTO directory.memberships (account_id, org_id, status, version, updated_at) VALUES ($1, $2, 'active', 1, now())",
            account, organization);
    }

    private Task AddRevokedSessionAsync(Guid account, string sessionId, DateTimeOffset revokedAt, DateTimeOffset keepUntil) =>
        ExecuteAsync(
            "INSERT INTO identity.revoked_sessions (session_id, account_id, revoked_at, keep_until) VALUES ($1, $2, $3, $4)",
            sessionId, account, revokedAt, keepUntil);

    private async Task<long> PendingMessagesAsync(string cellId) =>
        await ScalarAsync<long>(
            "SELECT count(*) FROM cell_management.outbound_messages WHERE cell_id = $1 AND type = 'SessionRevoked' AND acked_at IS NULL AND rejected_code IS NULL",
            cellId);

    private async Task<long> AttemptsAsync(string cellId) =>
        await ScalarAsync<long>(
            "SELECT coalesce(max(attempts), 0)::bigint FROM cell_management.outbound_messages WHERE cell_id = $1 AND type = 'SessionRevoked'", cellId);

    private async Task<string?> StateAsync(Guid messageId) =>
        await ScalarAsync<string?>(
            "SELECT CASE WHEN acked_at IS NOT NULL THEN 'acked' WHEN rejected_code IS NOT NULL THEN rejected_code ELSE 'pending' END FROM cell_management.outbound_messages WHERE message_id = $1",
            messageId);

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        for (var attempt = 0; attempt < 150 && !await condition(); attempt++)
        {
            await Task.Delay(100, CancellationToken);
        }

        (await condition()).Should().BeTrue();
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100", Justification = "Test queries are constants; values are parameters.")]
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

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100", Justification = "Test queries are constants; values are parameters.")]
    private async Task<T> ScalarAsync<T>(string sql, object parameter)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        var value = await command.ExecuteScalarAsync(CancellationToken);
        return value is T typed ? typed : default!;
    }
}
