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
/// Account statuses over the link (docs/contracts/phase-2.md, "Cell ↔ control plane link"): an <c>AccountStatusChanged</c> that Identity
/// queued reaches the cell's Presence contract and is acknowledged, a bad one is refused for good, and <c>FullSync</c> carries the
/// statuses of the cell's members (and no one else's), so a lost message or an emptied Valkey is repaired.
/// </summary>
[Trait("Category", "Privacy")]
[Collection(LinkWorkerCollection.Name)]
public sealed class AccountStatusLinkTests(PostgresDatabase database, KeycloakRealm keycloak) : IAsyncLifetime
{
    private WebApplication _controlPlane = null!;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _controlPlane = await ControlPlaneUnderTest.StartAsync(database, keycloak);

    public async ValueTask DisposeAsync() => await _controlPlane.DisposeAsync();

    [Fact]
    public async Task AccountStatusChanged_QueuedByIdentity_ReachesTheCellsPresenceAndIsAcknowledgedAsync()
    {
        await using var cell = await RegisterCellAsync();
        var account = Guid.NewGuid();
        await AddMembershipAsync(account, cell.CellId);
        var dnd = DateTimeOffset.UtcNow.AddMinutes(30);
        await SetStatusAsync(account, dnd, "🎯", "Focusing");

        await StartAsync(cell);
        try
        {
            await WaitUntilAsync(async () => await PendingMessagesAsync(cell.CellId) == 0);
            var applied = cell.Statuses.Applied.Should().ContainSingle("FullSync brings the same version again, which is not newer").Subject;
            applied.AccountId.Should().Be(account);
            applied.DndUntil.Should().BeCloseTo(dnd, TimeSpan.FromMilliseconds(1));
            applied.CustomEmoji.Should().Be("🎯");
            applied.CustomText.Should().Be("Focusing");
            applied.CustomExpiresAt.Should().BeNull();
            applied.TimeZone.Should().Be("Europe/Istanbul");
            applied.Version.Should().Be(1);
        }
        finally
        {
            await cell.Link.StopAsync(CancellationToken);
        }
    }

    [Fact]
    public async Task AccountStatusChanged_WithAnInvalidAccountOrVersion_IsRefusedForGoodAsync()
    {
        await using var cell = await RegisterCellAsync();
        var invalidAccount = await EnqueueAsync(cell.CellId, new AccountStatusChanged { AccountId = "not-an-id", Version = 1 });
        var noVersion = await EnqueueAsync(cell.CellId, new AccountStatusChanged { AccountId = Guid.NewGuid().ToString("D") });
        var longText = await EnqueueAsync(cell.CellId, new AccountStatusChanged
        {
            AccountId = Guid.NewGuid().ToString("D"),
            CustomText = new string('x', 801),
            Version = 1,
        });

        await StartAsync(cell);
        try
        {
            foreach (var message in new[] { invalidAccount, noVersion, longText })
            {
                await WaitUntilAsync(async () => await StateAsync(message) == "presence.account_status_invalid");
            }

            cell.Statuses.Applied.Should().BeEmpty();
        }
        finally
        {
            await cell.Link.StopAsync(CancellationToken);
        }
    }

    [Fact]
    public async Task FullSync_CarriesTheStatusesOfTheCellsMembersOnlyAsync()
    {
        await using var cell = await RegisterCellAsync();
        var otherCell = await RegisterCellAsync();
        var member = Guid.NewGuid();
        var otherCellsMember = Guid.NewGuid();
        await AddMembershipAsync(member, cell.CellId);
        await AddMembershipAsync(otherCellsMember, otherCell.CellId);

        // Rows without messages: only FullSync can bring them to the cell (a missed AccountStatusChanged).
        await AddStatusAsync(member, "Missed", version: 4);
        await AddStatusAsync(otherCellsMember, "Elsewhere", version: 2);

        await StartAsync(cell);
        try
        {
            await WaitUntilAsync(() => Task.FromResult(!cell.Statuses.Applied.IsEmpty));
            var applied = cell.Statuses.Applied.Should().ContainSingle("no other account's status is sent to the cell").Subject;
            applied.AccountId.Should().Be(member);
            applied.CustomText.Should().Be("Missed");
            applied.Version.Should().Be(4);
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

    private async Task SetStatusAsync(Guid account, DateTimeOffset dndUntil, string emoji, string text)
    {
        await using var scope = _controlPlane.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<AccountStatuses>().SetAsync(
            account, dndUntil, hasCustomStatus: true, emoji, text, customExpiresAt: null, "Europe/Istanbul", CancellationToken);
        result.IsSuccess.Should().BeTrue();
    }

    private async Task<Guid> EnqueueAsync(string cellId, AccountStatusChanged message)
    {
        await using var scope = _controlPlane.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await unitOfWork.BeginAsync(CancellationToken);
        var id = await scope.ServiceProvider.GetRequiredService<ICellLinkOutbox>().EnqueueAsync(cellId, new ConnectResponse { AccountStatusChanged = message }, CancellationToken);
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

    private Task AddStatusAsync(Guid account, string text, long version) =>
        ExecuteAsync(
            "INSERT INTO identity.account_status (account_id, custom_text, time_zone, version, updated_at) VALUES ($1, $2, 'UTC', $3, now())",
            account, text, version);

    private async Task<long> PendingMessagesAsync(string cellId) =>
        await ScalarAsync<long>(
            "SELECT count(*) FROM cell_management.outbound_messages WHERE cell_id = $1 AND type = 'AccountStatusChanged' AND acked_at IS NULL AND rejected_code IS NULL",
            cellId);

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
