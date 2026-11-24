using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Backroom.Modules.Identity.Application;
using Backroom.Modules.Identity.Contracts;
using Backroom.Testing.Postgres;

namespace Backroom.Modules.Identity.IntegrationTests;

/// <summary>
/// The cleanup of expired revocations and the revocations <c>FullSync</c> reports (docs/contracts/phase-1.md, "Data" and "Drift
/// repair"). The clock is a fake one in 2001 and the rows are written with timestamps of that year: the cleanup only reaches rows
/// that have expired by that clock, so other classes' rows (later years) are never touched and counts are exact.
/// </summary>
[Trait("Category", "Privacy")]
public sealed class RevokedSessionJobTests(PostgresDatabase database) : IAsyncLifetime
{
    private static readonly DateTimeOffset s_now = new(2001, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(s_now);
    private readonly LogoutTokenIssuer _issuer = new();
    private readonly TestData _data = new(database);
    private WebApplication _controlPlane = null!;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() =>
        _controlPlane = await IdentityUnderTest.StartAsync(database, CancellationToken, _time, _issuer.TrustedKey);

    public async ValueTask DisposeAsync()
    {
        await _controlPlane.DisposeAsync();
        _issuer.Dispose();
    }

    [Fact]
    public async Task Cleanup_DeletesRevocationsAfterTheirKeepUntilOnlyAsync()
    {
        var expired = Guid.NewGuid();
        var expiring = Guid.NewGuid();
        var kept = Guid.NewGuid();
        await _data.AddRevokedSessionAsync(expired, "s-expired", s_now.AddMinutes(-10), s_now.AddMinutes(-1));
        await _data.AddRevokedSessionAsync(expiring, "s-expiring", s_now.AddMinutes(-10), s_now);
        await _data.AddRevokedSessionAsync(kept, "s-kept", s_now.AddMinutes(-1), s_now.AddMinutes(4));

        var deleted = await _controlPlane.Services.GetRequiredService<RevokedSessionCleanup>().RunAsync(CancellationToken);

        deleted.Should().Be(2);
        (await _data.CountRevokedSessionsAsync(expired)).Should().Be(0);
        (await _data.CountRevokedSessionsAsync(expiring)).Should().Be(0, "a revocation is kept until keep_until, not after");
        (await _data.CountRevokedSessionsAsync(kept)).Should().Be(1);
    }

    [Fact]
    public async Task Cleanup_WithMoreRowsThanABatch_StopsAtTheRunLimitAndFinishesNextTimeAsync()
    {
        var accounts = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToList();
        foreach (var account in accounts)
        {
            await _data.AddRevokedSessionAsync(account, "s-batch", s_now.AddMinutes(-20), s_now.AddMinutes(-15));
        }

        var cleanup = _controlPlane.Services.GetRequiredService<RevokedSessionCleanup>();
        var first = await cleanup.RunAsync(batchSize: 2, maxBatches: 2, CancellationToken);
        var second = await cleanup.RunAsync(batchSize: 2, maxBatches: 2, CancellationToken);

        first.Should().Be(4, "two batches of two");
        second.Should().Be(1);
    }

    [Fact]
    public async Task Cleanup_AfterTheClockPassesKeepUntil_DeletesALogoutRecordedByTheEndpointAsync()
    {
        var account = Guid.NewGuid();
        await using (var scope = _controlPlane.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<SessionRevocations>().RevokeAsync(account, "s-timed", CancellationToken);
        }

        (await _data.CountRevokedSessionsAsync(account)).Should().Be(1);
        _time.Advance(SessionRevocationPolicy.KeepFor - TimeSpan.FromSeconds(1));
        await _controlPlane.Services.GetRequiredService<RevokedSessionCleanup>().RunAsync(CancellationToken);
        (await _data.CountRevokedSessionsAsync(account)).Should().Be(1, "tokens issued before the revocation can still be valid");

        _time.Advance(TimeSpan.FromSeconds(1));
        await _controlPlane.Services.GetRequiredService<RevokedSessionCleanup>().RunAsync(CancellationToken);
        (await _data.CountRevokedSessionsAsync(account)).Should().Be(0);
    }

    [Fact]
    public async Task FullSyncQuery_ReportsRevocationsInForceOfThisCellsMembersOnlyAsync()
    {
        var cell = await _data.AddCellAsync();
        var otherCell = await _data.AddCellAsync();
        var member = Guid.NewGuid();
        var bothCells = Guid.NewGuid();
        var elsewhere = Guid.NewGuid();
        var stranger = Guid.NewGuid();
        var expiredMember = Guid.NewGuid();
        await _data.AddMembershipAsync(member, cell);
        await _data.AddMembershipAsync(bothCells, cell);
        await _data.AddMembershipAsync(bothCells, otherCell);
        await _data.AddMembershipAsync(elsewhere, otherCell);
        await _data.AddMembershipAsync(expiredMember, cell);
        await _data.AddRevokedSessionAsync(member, "s-member", s_now.AddMinutes(-1), s_now.AddMinutes(4));
        await _data.AddRevokedSessionAsync(member, string.Empty, s_now.AddMinutes(-2), s_now.AddMinutes(3));
        await _data.AddRevokedSessionAsync(bothCells, "s-both", s_now.AddMinutes(-1), s_now.AddMinutes(4));
        await _data.AddRevokedSessionAsync(elsewhere, "s-elsewhere", s_now.AddMinutes(-1), s_now.AddMinutes(4));
        await _data.AddRevokedSessionAsync(stranger, "s-stranger", s_now.AddMinutes(-1), s_now.AddMinutes(4));
        await _data.AddRevokedSessionAsync(expiredMember, "s-old", s_now.AddMinutes(-10), s_now.AddMinutes(-1));

        // A call (FullSync) has a scope of its own, and a unit of work begins once per scope.
        var forCell = await FullSyncAsync(cell);
        var forOtherCell = await FullSyncAsync(otherCell);
        await _data.DeleteRevokedSessionsAsync(member, bothCells, elsewhere, stranger, expiredMember);

        forCell.Select(revoked => (revoked.AccountId, revoked.SessionId)).Should().BeEquivalentTo(
            [(member, (string?)"s-member"), (member, (string?)null), (bothCells, (string?)"s-both")]);
        forCell.Single(revoked => revoked.SessionId == "s-member").KeepUntil.Should().Be(s_now.AddMinutes(4));
        forOtherCell.Select(revoked => revoked.AccountId).Should().BeEquivalentTo([bothCells, elsewhere]);
    }
    [Fact]
    public async Task FullSyncQuery_WhenManyOtherAccountsSignedOutLater_StillReportsTheMembersOldestRevocationAsync()
    {
        var cell = await _data.AddCellAsync();
        var member = Guid.NewGuid();
        await _data.AddMembershipAsync(member, cell);
        await _data.AddRevokedSessionAsync(member, "s-old-member", s_now.AddMinutes(-3), s_now.AddMinutes(2));
        var strangers = await _data.AddStrangerRevocationsAsync(2 * RevokedSessionQueries.PageSize + 100, s_now.AddMinutes(-1), s_now.AddMinutes(4));

        try
        {
            var forCell = await FullSyncAsync(cell);

            forCell.Should().ContainSingle(revoked => revoked.AccountId == member && revoked.SessionId == "s-old-member",
                "the members' rows are found on a later page, not cut off by newer rows of other accounts");
            forCell.Should().OnlyContain(revoked => revoked.AccountId == member);
        }
        finally
        {
            await _data.DeleteRevokedSessionsAsync([member, .. strangers]);
        }
    }


    private async Task<IReadOnlyList<RevokedSession>> FullSyncAsync(string cellId)
    {
        await using var scope = _controlPlane.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IRevokedSessionQueries>().ListForCellAsync(cellId, CancellationToken);
    }
}
