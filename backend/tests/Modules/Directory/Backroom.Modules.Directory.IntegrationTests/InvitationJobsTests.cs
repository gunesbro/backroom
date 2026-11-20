using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using AwesomeAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Backroom.BuildingBlocks.Application.Persistence;
using Backroom.ControlPlane.Contracts.V1;
using Backroom.Modules.CellManagement.Contracts;
using Backroom.Modules.Directory.Application.Invitations;
using Backroom.Modules.Directory.Infrastructure;
using Backroom.Modules.Directory.Infrastructure.Invitations;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;

namespace Backroom.Modules.Directory.IntegrationTests;

/// <summary>
/// The control plane's scheduled invitation jobs (docs/contracts/phase-1.md, flow 3, step 8, and "Privacy and
/// retention") against real PostgreSQL. The clock is a fake one in 2001: the jobs only reach rows with timestamps of
/// that year, which only this class writes, so other classes' invitations are never touched and counts are exact.
/// Each test deletes its organization's invitations afterwards.
/// </summary>
[Trait("Category", "Privacy")]
public sealed class InvitationJobsTests(PostgresDatabase database, KeycloakRealm keycloak) : IAsyncLifetime
{
    private const string Cell = "cell-jobs";

    private static readonly DateTimeOffset s_now = new(2001, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(s_now);
    private readonly Guid _organization = Guid.CreateVersion7();
    private WebApplication _controlPlane = null!;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _controlPlane = await DirectoryUnderTest.StartAsync(database, keycloak, CancellationToken, _time);
        await ExecuteAsync(
            "INSERT INTO directory.organizations (id, name, region, cell_id, status, created_by, created_at) VALUES ($1, 'Acme', 'eu', $2, 'active', $1, now())",
            _organization, Cell);
    }

    public async ValueTask DisposeAsync()
    {
        await ExecuteAsync("DELETE FROM directory.invitations WHERE org_id = $1", _organization);
        await _controlPlane.DisposeAsync();
    }

    [Fact]
    public async Task Expire_MarksOnlyDuePendingInvitationsAsync()
    {
        var due = await AddAsync("pending", expiresAt: s_now.AddMinutes(-1));
        var dueNow = await AddAsync("pending", expiresAt: s_now);
        var notDue = await AddAsync("pending", expiresAt: s_now.AddMinutes(1));
        var accepting = await AddAsync("accepting", expiresAt: s_now.AddDays(-1));
        var used = await AddAsync("used", expiresAt: s_now.AddDays(-1));
        var revoked = await AddAsync("revoked", expiresAt: s_now.AddDays(-1));
        var failed = await AddAsync("failed", expiresAt: s_now.AddDays(-1));

        var expired = await Jobs().ExpireAsync(CancellationToken);

        expired.Should().Be(2);
        (await StatusAsync(due)).Should().Be("expired");
        (await StatusAsync(dueNow)).Should().Be("expired", "acceptance stops at expires_at, and so does the invitation");
        (await StatusAsync(notDue)).Should().Be("pending");
        (await StatusAsync(accepting)).Should().Be("accepting", "an acceptance in flight is settled only by the cell's answer");
        (await StatusAsync(used)).Should().Be("used");
        (await StatusAsync(revoked)).Should().Be("revoked");
        (await StatusAsync(failed)).Should().Be("failed");
        (await ScalarAsync<DateTime>("SELECT updated_at FROM directory.invitations WHERE invitation_id = $1", due))
            .Should().Be(s_now.UtcDateTime);
        (await ScalarAsync<byte[]>("SELECT email_hmac FROM directory.invitations WHERE invitation_id = $1", due))
            .Should().NotBeNull("the email HMAC is kept until the retention ends");
    }

    [Fact]
    public async Task Expire_WhileAnAcceptanceHoldsTheInvitationLock_SkipsItWithoutWaitingAndTheAcceptanceSucceedsAsync()
    {
        // The acceptance read the invitation before its expiry and holds its lock, as InvitationAcceptance does.
        var codeHash = RandomNumberGenerator.GetBytes(32);
        var held = await AddAsync("pending", expiresAt: s_now.AddMinutes(-1), codeHash: codeHash);
        var other = await AddAsync("pending", expiresAt: s_now.AddMinutes(-1));
        await using var acceptance = _controlPlane.Services.CreateAsyncScope();
        var unitOfWork = acceptance.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var registry = acceptance.ServiceProvider.GetRequiredService<IInvitationRegistry>();
        await unitOfWork.BeginAsync(CancellationToken);
        (await registry.LockByCodeAsync(codeHash, CancellationToken))!.Status.Should().Be("pending");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var expired = await Jobs().ExpireAsync(timeout.Token);

        expired.Should().Be(1);
        (await StatusAsync(other)).Should().Be("expired");
        (await registry.StartAcceptanceAsync(held, _organization, Guid.CreateVersion7(), s_now.AddMinutes(-2), CancellationToken))
            .Should().BeTrue("the job left the locked invitation alone");
        await unitOfWork.CommitAsync(CancellationToken);
        (await Jobs().ExpireAsync(CancellationToken)).Should().Be(0);
        (await StatusAsync(held)).Should().Be("accepting");
    }

    [Fact]
    public async Task Expire_WhileAnotherTransactionLocksTheRow_SkipsItWithoutWaitingAsync()
    {
        var locked = await AddAsync("pending", expiresAt: s_now.AddMinutes(-1));
        var other = await AddAsync("pending", expiresAt: s_now.AddMinutes(-1));
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(CancellationToken);
        await using (var lockRow = new NpgsqlCommand("SELECT 1 FROM directory.invitations WHERE invitation_id = $1 FOR UPDATE", connection, transaction))
        {
            lockRow.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = locked });
            await lockRow.ExecuteNonQueryAsync(CancellationToken);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var expired = await Jobs().ExpireAsync(timeout.Token);
        await transaction.RollbackAsync(CancellationToken);

        expired.Should().Be(1, "another instance's batch holds the first row");
        (await StatusAsync(other)).Should().Be("expired");
        (await StatusAsync(locked)).Should().Be("pending", "the next run picks it up");
        (await Jobs().ExpireAsync(CancellationToken)).Should().Be(1);
    }

    [Fact]
    public async Task Expire_WithMoreDueThanTheRunAllows_StopsAtTheLimitOldestFirstAsync()
    {
        var invitations = new List<Guid>();
        for (var index = 0; index < 7; index++)
        {
            invitations.Add(await AddAsync("pending", expiresAt: s_now.AddHours(-7 + index)));
        }

        var first = await Jobs().ExpireAsync(batchSize: 3, maxBatches: 2, CancellationToken);

        first.Should().Be(6);
        foreach (var invitation in invitations[..6])
        {
            (await StatusAsync(invitation)).Should().Be("expired");
        }

        (await StatusAsync(invitations[6])).Should().Be("pending", "the newest expiry waits for the next run");
        (await Jobs().ExpireAsync(batchSize: 3, maxBatches: 2, CancellationToken)).Should().Be(1);
    }

    [Fact]
    public async Task Expire_WithABusyInvitationInAFullBatch_GoesOnWithTheNextBatchAsync()
    {
        var codeHash = RandomNumberGenerator.GetBytes(32);
        var busy = await AddAsync("pending", expiresAt: s_now.AddHours(-10), codeHash: codeHash);
        var others = new List<Guid>();
        for (var index = 0; index < 4; index++)
        {
            others.Add(await AddAsync("pending", expiresAt: s_now.AddHours(-9 + index)));
        }

        await using var acceptance = _controlPlane.Services.CreateAsyncScope();
        var unitOfWork = acceptance.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await unitOfWork.BeginAsync(CancellationToken);
        await acceptance.ServiceProvider.GetRequiredService<IInvitationRegistry>().LockByCodeAsync(codeHash, CancellationToken);

        // Every batch of two selects the busy invitation and one other; a skipped row does not end the run.
        var expired = await Jobs().ExpireAsync(batchSize: 2, maxBatches: 3, CancellationToken);
        await unitOfWork.RollbackAsync(CancellationToken);

        expired.Should().Be(3);
        (await StatusAsync(busy)).Should().Be("pending");
        (await StatusAsync(others[3])).Should().Be("pending", "the run ended at its batch limit");
    }

    [Fact]
    public async Task Worker_AfterAFailedExpiry_StillAppliesRetentionAndExpiresOnTheNextRunAsync()
    {
        var failing = new FailingOnce();
        await using var controlPlane = await DirectoryUnderTest.StartAsync(
            database,
            keycloak,
            CancellationToken,
            _time,
            configureServices: services =>
            {
                services.AddDirectoryJobs();
                services.AddSingleton(failing);
                services.AddScoped<IInvitationRegistry>(provider =>
                    new ExpiryFailingRegistry(ActivatorUtilities.CreateInstance<InvitationRegistry>(provider), provider.GetRequiredService<FailingOnce>()));
            });
        var due = await AddAsync("pending", expiresAt: s_now.AddMinutes(-1));
        var ended = await AddAsync("expired", expiresAt: s_now.AddDays(-31), updatedAt: s_now.AddDays(-31));

        for (var attempt = 0; attempt < 100 && await EmailHmacAsync(ended) is not null; attempt++)
        {
            _time.Advance(InvitationJobsWorker.Interval);
            await Task.Delay(TimeSpan.FromMilliseconds(100), CancellationToken);
        }

        failing.Failed.Should().BeTrue();
        (await EmailHmacAsync(ended)).Should().BeNull("the failed expiry did not keep retention from running");
        for (var attempt = 0; attempt < 100 && await StatusAsync(due) != "expired"; attempt++)
        {
            _time.Advance(InvitationJobsWorker.Interval);
            await Task.Delay(TimeSpan.FromMilliseconds(100), CancellationToken);
        }

        (await StatusAsync(due)).Should().Be("expired", "the worker survived the failed run");
    }

    [Fact]
    public async Task Retention_ClearsTheEmailHmacOfEndedAndUsedInvitationsAfter30DaysOnlyAsync()
    {
        var old = s_now.AddDays(-30).AddMilliseconds(-1);
        var exactly = s_now.AddDays(-30);
        var recent = s_now.AddDays(-29);
        var expiredOld = await AddAsync("expired", expiresAt: old, updatedAt: old);
        var revokedOld = await AddAsync("revoked", expiresAt: s_now.AddDays(-25), updatedAt: old);
        var failedOld = await AddAsync("failed", expiresAt: s_now.AddDays(-25), updatedAt: old);
        var usedOld = await AddAsync("used", expiresAt: s_now.AddDays(-25), updatedAt: old);
        var expiredExactly = await AddAsync("expired", expiresAt: exactly, updatedAt: exactly);
        var expiredRecent = await AddAsync("expired", expiresAt: recent, updatedAt: recent);
        var pendingOld = await AddAsync("pending", expiresAt: s_now.AddDays(1), updatedAt: old);
        var acceptingOld = await AddAsync("accepting", expiresAt: s_now.AddDays(1), updatedAt: old);

        var (cleared, _) = await Jobs().ApplyRetentionAsync(CancellationToken);

        cleared.Should().Be(4);
        foreach (var invitation in new[] { expiredOld, revokedOld, failedOld, usedOld })
        {
            (await EmailHmacAsync(invitation)).Should().BeNull();
            (await StatusAsync(invitation)).Should().NotBeNull("the invitation itself stays, without the email");
        }

        (await EmailHmacAsync(expiredExactly)).Should().NotBeNull("it is kept for 30 days, the last instant included");
        foreach (var invitation in new[] { expiredRecent, pendingOld, acceptingOld })
        {
            (await EmailHmacAsync(invitation)).Should().NotBeNull();
        }

        (await StatusAsync(pendingOld)).Should().Be("pending");
        (await StatusAsync(acceptingOld)).Should().Be("accepting");
        (await Jobs().ApplyRetentionAsync(CancellationToken)).EmailHmacsCleared.Should().Be(0);
    }

    [Fact]
    public async Task Retention_DeletesTombstonesOnlyOnceALateRegistrationWouldBeRefusedAsync()
    {
        var age = InvitationJobs.TombstoneRetention;
        age.Should().Be(TimeSpan.FromDays(37) + TimeSpan.FromHours(1));
        var oldTombstone = await AddTombstoneAsync(s_now - age - TimeSpan.FromMilliseconds(1));
        var exactTombstone = await AddTombstoneAsync(s_now - age);
        var recentTombstone = await AddTombstoneAsync(s_now.AddDays(-31));
        var revokedOld = await AddAsync("revoked", expiresAt: s_now.AddDays(-40), updatedAt: s_now.AddDays(-40));

        var (cleared, deleted) = await Jobs().ApplyRetentionAsync(CancellationToken);

        deleted.Should().Be(1);
        (await StatusAsync(oldTombstone)).Should().BeNull();
        (await StatusAsync(exactTombstone)).Should().Be("revoked");
        (await StatusAsync(recentTombstone)).Should().Be("revoked", "a late registration must still find it");
        cleared.Should().Be(1);
        (await StatusAsync(revokedOld)).Should().Be("revoked", "only tombstones are deleted; a revoked invitation loses only its email HMAC");
    }

    [Fact]
    public async Task Register_LateAfterItsTombstoneWasDeleted_IsRefusedAndRecreatesNothingAsync()
    {
        // The invitation was created before its revocation, so it expires at most MaxLifetime after it.
        var revokedAt = s_now - InvitationJobs.TombstoneRetention - TimeSpan.FromMilliseconds(1);
        var tombstone = await AddTombstoneAsync(revokedAt);
        (await Jobs().ApplyRetentionAsync(CancellationToken)).TombstonesDeleted.Should().Be(1);

        var refused = await RegisterAsync(tombstone, expiresAt: revokedAt + InvitationLinkMessages.MaxLifetime);

        refused.Should().Be(InvitationLinkMessages.InvalidCode);
        (await StatusAsync(tombstone)).Should().BeNull();
    }

    [Fact]
    public async Task Retention_WhileRowsAreLocked_SkipsThemWithoutWaitingAsync()
    {
        var locked = await AddAsync("expired", expiresAt: s_now.AddDays(-40), updatedAt: s_now.AddDays(-40));
        var other = await AddAsync("expired", expiresAt: s_now.AddDays(-40), updatedAt: s_now.AddDays(-40));
        var heldTombstone = await AddTombstoneAsync(s_now.AddDays(-40));
        var otherTombstone = await AddTombstoneAsync(s_now.AddDays(-40));
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(CancellationToken);
        await using (var lockRow = new NpgsqlCommand("SELECT 1 FROM directory.invitations WHERE invitation_id = $1 FOR UPDATE", connection, transaction))
        {
            lockRow.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = locked });
            await lockRow.ExecuteNonQueryAsync(CancellationToken);
        }

        // A repeated revocation of the tombstone's invitation holds its advisory lock, as every link message does.
        await using var revocation = _controlPlane.Services.CreateAsyncScope();
        var unitOfWork = revocation.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await unitOfWork.BeginAsync(CancellationToken);
        (await revocation.ServiceProvider.GetRequiredService<IInvitationRegistry>()
            .RevokeAsync(_organization, heldTombstone, Cell, s_now, CancellationToken)).Should().Be(RegistryOutcome.Ignored);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var (cleared, deleted) = await Jobs().ApplyRetentionAsync(timeout.Token);
        await transaction.RollbackAsync(CancellationToken);
        await unitOfWork.CommitAsync(CancellationToken);

        cleared.Should().Be(1);
        (await EmailHmacAsync(other)).Should().BeNull();
        (await EmailHmacAsync(locked)).Should().NotBeNull("the next run clears it");
        deleted.Should().Be(1);
        (await StatusAsync(otherTombstone)).Should().BeNull();
        (await StatusAsync(heldTombstone)).Should().Be("revoked");
        (await Jobs().ApplyRetentionAsync(CancellationToken)).Should().Be((1, 1));
    }

    [Fact]
    public async Task Retention_WithMoreThanTheRunAllows_StopsAtTheLimitAsync()
    {
        for (var index = 0; index < 5; index++)
        {
            await AddAsync("expired", expiresAt: s_now.AddDays(-40), updatedAt: s_now.AddDays(-40).AddMinutes(index));
            await AddTombstoneAsync(s_now.AddDays(-40).AddMinutes(index));
        }

        var first = await Jobs().ApplyRetentionAsync(batchSize: 2, maxBatches: 2, CancellationToken);
        var second = await Jobs().ApplyRetentionAsync(batchSize: 2, maxBatches: 2, CancellationToken);

        first.Should().Be((4, 4));
        second.Should().Be((1, 1));
    }

    [Fact]
    public async Task Worker_RunsTheJobsOnItsIntervalAsync()
    {
        await using var controlPlane = await DirectoryUnderTest.StartAsync(
            database, keycloak, CancellationToken, _time, configureServices: services => services.AddDirectoryJobs());
        var due = await AddAsync("pending", expiresAt: s_now.AddMinutes(-1));
        var ended = await AddAsync("expired", expiresAt: s_now.AddDays(-31), updatedAt: s_now.AddDays(-31));

        // Advancing on every poll: the worker may still be in its first run when the time first moves.
        for (var attempt = 0; attempt < 100 && (await StatusAsync(due) != "expired" || await EmailHmacAsync(ended) is not null); attempt++)
        {
            _time.Advance(InvitationJobsWorker.Interval);
            await Task.Delay(TimeSpan.FromMilliseconds(100), CancellationToken);
        }

        (await StatusAsync(due)).Should().Be("expired");
        (await EmailHmacAsync(ended)).Should().BeNull();
    }

    private InvitationJobs Jobs() => _controlPlane.Services.GetRequiredService<InvitationJobs>();

    private async Task<Guid> AddAsync(string status, DateTimeOffset expiresAt, DateTimeOffset? updatedAt = null, byte[]? codeHash = null)
    {
        var invitation = Guid.CreateVersion7();
        var accepted = status is "accepting" or "used";
        await ExecuteAsync(
            """
            INSERT INTO directory.invitations
                (invitation_id, org_id, cell_id, code_hash, email_hmac, role, expires_at, status, code_version,
                 accepting_account_id, accepted_at, failure_code, revoked_at, used_at, created_at, updated_at)
            VALUES ($1, $2, $3, $4, $5, 'member', $6, $7, 1,
                    CASE WHEN $8 THEN $1 END, CASE WHEN $8 THEN $9 END,
                    CASE WHEN $7 = 'failed' THEN 'organization.invitation_expired' END,
                    CASE WHEN $7 = 'revoked' THEN $9 END, CASE WHEN $7 = 'used' THEN $9 END, $9, $9)
            """,
            invitation, _organization, Cell, codeHash ?? RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32),
            expiresAt, status, accepted, updatedAt ?? s_now.AddDays(-1));
        return invitation;
    }

    // As InvitationRegistry leaves one for a revocation of an unknown invitation.
    private async Task<Guid> AddTombstoneAsync(DateTimeOffset revokedAt)
    {
        var invitation = Guid.CreateVersion7();
        await ExecuteAsync(
            "INSERT INTO directory.invitations (invitation_id, org_id, cell_id, status, revoked_at, created_at, updated_at) VALUES ($1, $2, $3, 'revoked', $4, $4, $4)",
            invitation, _organization, Cell, revokedAt);
        return invitation;
    }

    // InvitationRegistered from the cell, handled as the link handles it; the refusal code, or null if stored.
    private async Task<string?> RegisterAsync(Guid invitation, DateTimeOffset expiresAt)
    {
        await using var scope = _controlPlane.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await unitOfWork.BeginAsync(CancellationToken);
        var payload = new InvitationRegistered
        {
            OrganizationId = _organization.ToString("D"),
            InvitationId = invitation.ToString("D"),
            CodeHash = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)),
            Email = "kim@example.test",
            Role = "member",
            ExpiresAt = Timestamp.FromDateTimeOffset(expiresAt),
            CodeVersion = 1,
        };
        try
        {
            await scope.ServiceProvider.GetRequiredService<InvitationLinkMessages>()
                .HandleAsync(new CellMessage<InvitationRegistered>(Guid.CreateVersion7(), Cell, payload), CancellationToken);
        }
        catch (CellMessageRejectedException rejected)
        {
            await unitOfWork.RollbackAsync(CancellationToken);
            return rejected.Code;
        }

        await unitOfWork.CommitAsync(CancellationToken);
        return null;
    }

    private Task<string?> StatusAsync(Guid invitation) =>
        ScalarAsync<string>("SELECT status FROM directory.invitations WHERE invitation_id = $1", invitation);

    private Task<byte[]?> EmailHmacAsync(Guid invitation) =>
        ScalarAsync<byte[]>("SELECT email_hmac FROM directory.invitations WHERE invitation_id = $1", invitation);

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

    private sealed class FailingOnce
    {
        private int _failed;

        public bool Failed => Volatile.Read(ref _failed) == 1;

        public bool TryFail() => Interlocked.Exchange(ref _failed, 1) == 0;
    }

    // The real registry, except that the first expiry batch fails as a database outage would.
    private sealed class ExpiryFailingRegistry(IInvitationRegistry inner, FailingOnce failing) : IInvitationRegistry
    {
        public Task<InvitationJobBatch> ExpireDueAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken) =>
            failing.TryFail() ? throw new NpgsqlException("Simulated outage.") : inner.ExpireDueAsync(now, limit, cancellationToken);

        public Task<RegistryOutcome> RegisterAsync(InvitationRegistration registration, DateTimeOffset now, CancellationToken cancellationToken) =>
            inner.RegisterAsync(registration, now, cancellationToken);

        public Task<RegistryOutcome> RevokeAsync(Guid organizationId, Guid invitationId, string cellId, DateTimeOffset now, CancellationToken cancellationToken) =>
            inner.RevokeAsync(organizationId, invitationId, cellId, now, cancellationToken);

        public Task<InvitationByCode?> FindByCodeAsync(byte[] codeHash, CancellationToken cancellationToken) =>
            inner.FindByCodeAsync(codeHash, cancellationToken);

        public Task<InvitationForAcceptance?> LockByCodeAsync(byte[] codeHash, CancellationToken cancellationToken) =>
            inner.LockByCodeAsync(codeHash, cancellationToken);

        public Task<bool> StartAcceptanceAsync(Guid invitationId, Guid organizationId, Guid accountId, DateTimeOffset now, CancellationToken cancellationToken) =>
            inner.StartAcceptanceAsync(invitationId, organizationId, accountId, now, cancellationToken);

        public Task<Guid?> CompleteAcceptanceAsync(Guid organizationId, Guid invitationId, string cellId, DateTimeOffset now, CancellationToken cancellationToken) =>
            inner.CompleteAcceptanceAsync(organizationId, invitationId, cellId, now, cancellationToken);

        public Task<Guid?> FailAcceptanceAsync(Guid organizationId, Guid invitationId, string cellId, string code, DateTimeOffset now, CancellationToken cancellationToken) =>
            inner.FailAcceptanceAsync(organizationId, invitationId, cellId, code, now, cancellationToken);

        public Task<InvitationAcceptanceState?> FindLatestAcceptanceAsync(Guid accountId, CancellationToken cancellationToken) =>
            inner.FindLatestAcceptanceAsync(accountId, cancellationToken);

        public Task<InvitationJobBatch> ClearEndedEmailHmacsAsync(DateTimeOffset endedBefore, DateTimeOffset now, int limit, CancellationToken cancellationToken) =>
            inner.ClearEndedEmailHmacsAsync(endedBefore, now, limit, cancellationToken);

        public Task<InvitationJobBatch> DeleteTombstonesAsync(DateTimeOffset revokedBefore, int limit, CancellationToken cancellationToken) =>
            inner.DeleteTombstonesAsync(revokedBefore, limit, cancellationToken);
    }
}
