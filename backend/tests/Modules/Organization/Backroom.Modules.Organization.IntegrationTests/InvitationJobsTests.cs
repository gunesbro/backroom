using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Backroom.BuildingBlocks.Infrastructure.Maintenance;
using Backroom.Modules.Organization.Infrastructure.Migrations;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Backroom.Modules.Organization.Infrastructure.Invitations;
using Backroom.Testing;
using Backroom.Testing.Postgres;

namespace Backroom.Modules.Organization.IntegrationTests;

/// <summary>
/// The cell's scheduled invitation jobs (docs/contracts/phase-1.md, flow 3, step 8, and "Privacy and retention")
/// against real PostgreSQL, run as the maintenance role like the worker (docs/adr/0010). The clock is a fake one in 2001: the jobs only
/// reach rows with timestamps of that year, which only this class writes, so other classes' invitations are never
/// touched and counts are exact. Each test deletes its organization's rows afterwards.
/// </summary>
[Trait("Category", "Privacy")]
public sealed class InvitationJobsTests(PostgresDatabase database) : IAsyncLifetime
{
    private static readonly DateTimeOffset s_now = new(2001, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(s_now);
    private readonly Guid _organization = Guid.CreateVersion7();
    private readonly List<NpgsqlDataSource> _dataSources = [];

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await OrganizationSchema.MigrateOnceAsync(database, CancellationToken);
        await ExecuteAsync(
            "INSERT INTO organization.organizations (id, org_id, name, owner_account_id, version, created_at) VALUES ($1, $1, 'Acme', $2, 1, $3)",
            _organization, Guid.CreateVersion7(), s_now.AddYears(-1));
    }

    public async ValueTask DisposeAsync()
    {
        await ExecuteAsync("DELETE FROM organization.invitations WHERE org_id = $1", _organization);
        await ExecuteAsync("DELETE FROM organization.organizations WHERE id = $1", _organization);
        foreach (var dataSource in _dataSources)
        {
            await dataSource.DisposeAsync();
        }
    }

    [Fact]
    public async Task Expire_BeforeTwentyFourHoursAfterTheExpiry_LeavesPendingInvitationsAsync()
    {
        var justExpired = await AddAsync("pending", expiresAt: s_now);
        var hourAgo = await AddAsync("pending", expiresAt: s_now.AddHours(-1));
        var almost = await AddAsync("pending", expiresAt: s_now.AddHours(-24).AddSeconds(1));

        var expired = await RunAsync(Expiry(), CancellationToken);

        expired.Should().Be(0);
        foreach (var invitation in new[] { justExpired, hourAgo, almost })
        {
            (await RowAsync(invitation)).Status.Should().Be("pending", "a late-delivered acceptance must still succeed");
        }
    }

    [Fact]
    public async Task Expire_TwentyFourHoursAfterTheExpiry_MarksOnlyPendingInvitationsExpiredAsync()
    {
        var due = await AddAsync("pending", expiresAt: s_now.AddHours(-24).AddSeconds(-1));
        var dueExactly = await AddAsync("pending", expiresAt: s_now.AddHours(-24));
        var notDue = await AddAsync("pending", expiresAt: s_now.AddHours(-23));
        var accepted = await AddAsync("accepted", expiresAt: s_now.AddDays(-10));
        var revoked = await AddAsync("revoked", expiresAt: s_now.AddDays(-10), revokedAt: s_now.AddDays(-9));

        var expired = await RunAsync(Expiry(), CancellationToken);

        expired.Should().Be(2);
        var row = await RowAsync(due);
        row.Status.Should().Be("expired");
        row.ExpiredAt.Should().Be(s_now);
        row.Email.Should().NotBeNull("the email is kept until the retention ends");
        (await RowAsync(dueExactly)).Status.Should().Be("expired");
        (await RowAsync(notDue)).Status.Should().Be("pending");
        (await RowAsync(accepted)).Should().Match<(string Status, DateTimeOffset? ExpiredAt, string? Email)>(r => r.Status == "accepted" && r.ExpiredAt == null && r.Email != null, "accepted invitations are untouched");
        (await RowAsync(revoked)).Status.Should().Be("revoked");
        (await RunAsync(Expiry(), CancellationToken)).Should().Be(0, "a second run finds nothing more");
    }

    [Fact]
    public async Task Expire_PublishesNoEventAndLogsIdsOnlyAsync()
    {
        var invitation = await AddAsync("pending", expiresAt: s_now.AddDays(-2));
        var logger = new CapturingLogger<InvitationExpiryJob>();

        await RunAsync(Expiry(logger), CancellationToken);

        (await ScalarAsync<long>("SELECT count(*) FROM organization.outbox")).Should().Be(0, "the contract names no event for the cell's expiry");
        logger.Messages.Should().Contain(message => message.Contains(invitation.ToString(), StringComparison.Ordinal) && message.Contains(_organization.ToString(), StringComparison.Ordinal));
        logger.Messages.Should().NotContain(message => message.Contains("example.test", StringComparison.Ordinal), "no email in the log");
    }

    [Fact]
    public async Task Expire_WithMoreDueThanTheRunAllows_StopsAtTheLimitOldestFirstAsync()
    {
        var invitations = new List<Guid>();
        for (var index = 0; index < 7; index++)
        {
            invitations.Add(await AddAsync("pending", expiresAt: s_now.AddDays(-10).AddHours(index)));
        }

        var first = await RunAsync(Expiry(batchSize: 3), CancellationToken, maxBatches: 2);

        first.Should().Be(6);
        foreach (var invitation in invitations[..6])
        {
            (await RowAsync(invitation)).Status.Should().Be("expired");
        }

        (await RowAsync(invitations[6])).Status.Should().Be("pending", "the newest expiry waits for the next run");
        (await RunAsync(Expiry(batchSize: 3), CancellationToken, maxBatches: 2)).Should().Be(1);
    }

    [Fact]
    public async Task Expire_WhileAnotherTransactionLocksTheRow_SkipsItWithoutWaitingAsync()
    {
        var locked = await AddAsync("pending", expiresAt: s_now.AddDays(-5));
        var other = await AddAsync("pending", expiresAt: s_now.AddDays(-4));
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(CancellationToken);
        await using (var lockRow = new NpgsqlCommand("SELECT 1 FROM organization.invitations WHERE id = $1 FOR UPDATE", connection, transaction))
        {
            lockRow.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = locked });
            await lockRow.ExecuteNonQueryAsync(CancellationToken);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var expired = await RunAsync(Expiry(), timeout.Token);
        await transaction.RollbackAsync(CancellationToken);

        expired.Should().Be(1, "the batch skipped the row another transaction holds");
        (await RowAsync(other)).Status.Should().Be("expired");
        (await RowAsync(locked)).Status.Should().Be("pending", "the next run picks it up");
        (await RunAsync(Expiry(), CancellationToken)).Should().Be(1);
    }

    [Fact]
    public async Task Expire_TwoRunsAtOnce_ExpireEachInvitationExactlyOnceAsync()
    {
        const int Count = 40;
        for (var index = 0; index < Count; index++)
        {
            await AddAsync("pending", expiresAt: s_now.AddDays(-3).AddMinutes(index));
        }

        // Small batches, so the runs interleave.
        var runs = await Task.WhenAll(
            RunAsync(Expiry(batchSize: 5), CancellationToken, maxBatches: 20),
            RunAsync(Expiry(batchSize: 5), CancellationToken, maxBatches: 20));

        runs.Sum().Should().Be(Count, "no invitation is processed twice and none is missed");
        (await ScalarAsync<long>("SELECT count(*) FROM organization.invitations WHERE org_id = $1 AND status = 'expired'", _organization)).Should().Be(Count);
    }

    [Fact]
    public async Task ApplyRetention_ThirtyDaysAfterExpiredOrRevoked_DeletesTheEmailAndKeepsTheRowAsync()
    {
        var expiredOld = await AddAsync("expired", expiresAt: s_now.AddDays(-50), expiredAt: s_now.AddDays(-30));
        var expiredYoung = await AddAsync("expired", expiresAt: s_now.AddDays(-50), expiredAt: s_now.AddDays(-30).AddSeconds(1));
        var revokedOld = await AddAsync("revoked", expiresAt: s_now.AddDays(-5), revokedAt: s_now.AddDays(-31));
        var revokedYoung = await AddAsync("revoked", expiresAt: s_now.AddDays(-5), revokedAt: s_now.AddDays(-29));
        var acceptedOld = await AddAsync("accepted", expiresAt: s_now.AddDays(-90));
        var pendingOld = await AddAsync("pending", expiresAt: s_now.AddDays(-90));

        var cleared = await RunAsync(Retention(), CancellationToken);

        cleared.Should().Be(2);

        (await RowAsync(expiredOld)).Should().Match<(string Status, DateTimeOffset? ExpiredAt, string? Email)>(r => r.Status == "expired" && r.Email == null);
        (await RowAsync(revokedOld)).Status.Should().Be("revoked");
        (await RowAsync(revokedOld)).Email.Should().BeNull();
        (await RowAsync(expiredYoung)).Email.Should().NotBeNull("30 days have not passed");
        (await RowAsync(revokedYoung)).Email.Should().NotBeNull("30 days have not passed");
        (await RowAsync(acceptedOld)).Email.Should().NotBeNull("accepted invitations are untouched");
        (await RowAsync(pendingOld)).Email.Should().NotBeNull("a pending invitation needs its email until it is marked expired");
        (await RunAsync(Retention(), CancellationToken)).Should().Be(0);

        _time.Advance(TimeSpan.FromSeconds(2));
        (await RunAsync(Retention(), CancellationToken)).Should().Be(1, "the young expired one is now 30 days old");
        (await RowAsync(expiredYoung)).Email.Should().BeNull();
    }

    [Fact]
    public async Task ApplyRetention_WithBothBacklogsLargerThanABatch_ErasesBothInOneRunAsync()
    {
        var expired = new List<Guid>();
        var revoked = new List<Guid>();
        for (var index = 0; index < 5; index++)
        {
            expired.Add(await AddAsync("expired", expiresAt: s_now.AddDays(-60), expiredAt: s_now.AddDays(-40).AddHours(index)));
            revoked.Add(await AddAsync("revoked", expiresAt: s_now.AddDays(-60), revokedAt: s_now.AddDays(-40).AddHours(index)));
        }

        (await RunAsync(Retention(batchSize: 2), CancellationToken, maxBatches: 2)).Should().Be(4);

        (await ErasedCountAsync(expired)).Should().BeGreaterThan(0, "revoked rows must not starve expired ones");
        (await ErasedCountAsync(revoked)).Should().BeGreaterThan(0, "expired rows must not starve revoked ones");
    }

    private async Task<long> ErasedCountAsync(List<Guid> invitations) =>
        await ScalarAsync<long>("SELECT count(*) FROM organization.invitations WHERE id = ANY($1) AND email_erased_at IS NOT NULL", invitations.ToArray());

    [Fact]
    public async Task ApplyRetention_WithMoreDueThanTheRunAllows_StopsAtTheLimitAsync()
    {
        for (var index = 0; index < 5; index++)
        {
            await AddAsync("expired", expiresAt: s_now.AddDays(-60), expiredAt: s_now.AddDays(-40).AddHours(index));
        }

        (await RunAsync(Retention(batchSize: 2), CancellationToken, maxBatches: 2)).Should().Be(4);
        (await RunAsync(Retention(batchSize: 2), CancellationToken, maxBatches: 2)).Should().Be(1);
    }

    [Fact]
    public async Task Jobs_UnderTurkishCulture_BehaveTheSameAsync()
    {
        using var culture = CultureScope.Turkish();
        var due = await AddAsync("pending", expiresAt: s_now.AddDays(-2), email: "ISIK@example.test");
        var old = await AddAsync("revoked", expiresAt: s_now.AddDays(-60), revokedAt: s_now.AddDays(-40), email: "IIIK@example.test");

        (await RunAsync(Expiry(), CancellationToken)).Should().Be(1);
        (await RunAsync(Retention(), CancellationToken)).Should().Be(1);

        (await RowAsync(due)).Status.Should().Be("expired");
        (await RowAsync(old)).Email.Should().BeNull();
    }

    [Fact]
    public async Task ApplyRetention_SetsTheErasedMarkerAndASecondRunDoesNothingAsync()
    {
        var expired = await AddAsync("expired", expiresAt: s_now.AddDays(-50), expiredAt: s_now.AddDays(-31));
        var revoked = await AddAsync("revoked", expiresAt: s_now.AddDays(-5), revokedAt: s_now.AddDays(-31));
        var young = await AddAsync("expired", expiresAt: s_now.AddDays(-50), expiredAt: s_now.AddDays(-29));

        (await RunAsync(Retention(), CancellationToken)).Should().Be(2);

        (await ErasedAtAsync(expired)).Should().Be(s_now);
        (await ErasedAtAsync(revoked)).Should().Be(s_now);
        (await ErasedAtAsync(young)).Should().BeNull("the retention has not ended");
        _time.Advance(TimeSpan.FromHours(1));
        (await RunAsync(Retention(), CancellationToken)).Should().Be(0, "erased rows are not visited again");
        (await ErasedAtAsync(expired)).Should().Be(s_now, "the marker keeps the time of the erasure");
    }

    [Fact]
    public async Task MeasureBacklog_CountsDueRowsAndReportsWhenTheOldestBecameDueAsync()
    {
        await AddAsync("pending", expiresAt: s_now.AddDays(-5));
        await AddAsync("pending", expiresAt: s_now.AddDays(-3));
        await AddAsync("pending", expiresAt: s_now.AddHours(-1));
        await AddAsync("expired", expiresAt: s_now.AddDays(-60), expiredAt: s_now.AddDays(-40));
        await AddAsync("revoked", expiresAt: s_now.AddDays(-60), revokedAt: s_now.AddDays(-35));
        await AddAsync("expired", expiresAt: s_now.AddDays(-60), expiredAt: s_now.AddDays(-40), erasedAt: s_now.AddDays(-10));
        await using var connection = await OpenMaintenanceAsync();

        var expiry = await Expiry().MeasureBacklogAsync(connection, CancellationToken);
        var retention = await Retention().MeasureBacklogAsync(connection, CancellationToken);

        expiry.Count.Should().Be(2);
        expiry.OldestDue.Should().Be(s_now.AddDays(-5) + InvitationExpiryJob.ExpiryGrace);
        retention.Count.Should().Be(2, "an erased row is not due");
        retention.OldestDue.Should().Be(s_now.AddDays(-40) + InvitationRetentionJob.Retention);
    }

    [Fact]
    public async Task Backfill_MarksRowsWhoseEmailWasAlreadyErasedAsync()
    {
        var expired = await AddAsync("expired", expiresAt: s_now.AddDays(-60), expiredAt: s_now.AddDays(-40), noEmail: true);
        var revoked = await AddAsync("revoked", expiresAt: s_now.AddDays(-60), revokedAt: s_now.AddDays(-35), noEmail: true);
        var kept = await AddAsync("expired", expiresAt: s_now.AddDays(-60), expiredAt: s_now.AddDays(-10));

        await ExecuteAsync(EraseInvitationEmailsByMarker.BackfillSql);

        (await ErasedAtAsync(expired)).Should().Be(s_now.AddDays(-40));
        (await ErasedAtAsync(revoked)).Should().Be(s_now.AddDays(-35));
        (await ErasedAtAsync(kept)).Should().BeNull("it still holds its email");
        (await RunAsync(Retention(), CancellationToken)).Should().Be(0, "the backfilled rows are not visited");
    }

    [Fact]
    public async Task RelayRole_CannotReadOrChangeInvitationsAsync()
    {
        var invitation = await AddAsync("pending", expiresAt: s_now);
        await using var dataSource = NpgsqlDataSource.Create(database.RelayConnectionString);

        foreach (var sql in new[]
        {
            "SELECT id FROM organization.invitations WHERE id = $1",
            "SELECT status FROM organization.invitations WHERE id = $1",
            "SELECT email FROM organization.invitations WHERE id = $1",
            "SELECT code_hash FROM organization.invitations WHERE id = $1",
            "UPDATE organization.invitations SET status = 'expired' WHERE id = $1",
            "UPDATE organization.invitations SET email = NULL WHERE id = $1",
        })
        {
            await using var command = dataSource.CreateCommand(sql);
            command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = invitation });
            var run = async () => await command.ExecuteScalarAsync(CancellationToken);

            (await run.Should().ThrowAsync<PostgresException>(sql)).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege, sql);
        }
    }

    [Fact]
    [SuppressMessage("Security", "CA2100", Justification = "Test SQL; the column names are constants of this test.")]
    public async Task MaintenanceRole_CannotReadTheEmailCodeHashOrSaltAsync()
    {
        var invitation = await AddAsync("pending", expiresAt: s_now);
        await using var connection = await OpenMaintenanceAsync();

        foreach (var column in new[] { "email", "code_hash", "salt", "*" })
        {
            await using var command = new NpgsqlCommand($"SELECT {column} FROM organization.invitations WHERE id = $1", connection);
            command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = invitation });
            var run = async () => await command.ExecuteScalarAsync(CancellationToken);

            (await run.Should().ThrowAsync<PostgresException>(column)).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege, column);
        }

        await using var filter = new NpgsqlCommand("SELECT id FROM organization.invitations WHERE email IS NOT NULL", connection);
        var filtered = async () => await filter.ExecuteScalarAsync(CancellationToken);
        (await filtered.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege, "filtering on email needs SELECT too");
    }

    private InvitationExpiryJob Expiry(ILogger<InvitationExpiryJob>? logger = null, int batchSize = InvitationExpiryJob.DefaultBatchSize) =>
        new(_time, logger ?? NullLogger<InvitationExpiryJob>.Instance, batchSize);

    private InvitationRetentionJob Retention(int batchSize = InvitationRetentionJob.DefaultBatchSize) =>
        new(_time, NullLogger<InvitationRetentionJob>.Instance, batchSize);

    private async Task<NpgsqlConnection> OpenMaintenanceAsync()
    {
        var dataSource = NpgsqlDataSource.Create(database.MaintenanceConnectionString);
        _dataSources.Add(dataSource);
        return await dataSource.OpenConnectionAsync(CancellationToken);
    }

    // What the runner does for one run: batches, each in its own transaction, until one is short or the limit is reached.
    private async Task<int> RunAsync(IMaintenanceJob job, CancellationToken cancellationToken, int maxBatches = 20)
    {
        await using var connection = await OpenMaintenanceAsync();
        var total = 0;
        for (var batch = 0; batch < maxBatches; batch++)
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var handled = await job.RunBatchAsync(connection, transaction, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            total += handled;
            if (handled < job.BatchSize)
            {
                break;
            }
        }

        return total;
    }

    private async Task<DateTimeOffset?> ErasedAtAsync(Guid invitation)
    {
        var value = await ScalarAsync<object>("SELECT email_erased_at FROM organization.invitations WHERE id = $1", invitation);
        return value is DateTime moment ? new DateTimeOffset(moment.ToUniversalTime()) : null;
    }

    private async Task<Guid> AddAsync(string status, DateTimeOffset expiresAt, DateTimeOffset? expiredAt = null, DateTimeOffset? revokedAt = null, string? email = null, DateTimeOffset? erasedAt = null, bool noEmail = false)
    {
        var invitation = Guid.CreateVersion7();
        await ExecuteAsync(
            """
            INSERT INTO organization.invitations
                (id, org_id, email, role, salt, key_version, code_version, code_hash, status, created_by, created_at, expires_at, expired_at, revoked_at, email_erased_at)
            VALUES ($1, $2, $3, 'member', $4, 1, 1, $5, $6, $7, $8, $9, $10, $11, $12)
            """,
            invitation, _organization, noEmail ? DBNull.Value : email ?? $"{status}-{invitation:N}@example.test", RandomNumberGenerator.GetBytes(16), RandomNumberGenerator.GetBytes(32), status,
            Guid.CreateVersion7(), expiresAt.AddDays(-7), expiresAt, (object?)expiredAt ?? DBNull.Value, (object?)revokedAt ?? DBNull.Value, (object?)erasedAt ?? DBNull.Value);
        return invitation;
    }

    private async Task<(string Status, DateTimeOffset? ExpiredAt, string? Email)> RowAsync(Guid invitation)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand("SELECT status, expired_at, email FROM organization.invitations WHERE id = $1", connection);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = invitation });
        await using var reader = await command.ExecuteReaderAsync(CancellationToken);
        await reader.ReadAsync(CancellationToken);
        return (
            reader.GetString(0),
            await reader.IsDBNullAsync(1, CancellationToken) ? null : reader.GetFieldValue<DateTimeOffset>(1),
            await reader.IsDBNullAsync(2, CancellationToken) ? null : reader.GetString(2));
    }

    [SuppressMessage("Security", "CA2100", Justification = "Test SQL is constant; values are parameters.")]
    private async Task<T> ScalarAsync<T>(string sql, params object[] values)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }

        return (T)(await command.ExecuteScalarAsync(CancellationToken))!;
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

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_messages)
                {
                    return [.. _messages];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            lock (_messages)
            {
                _messages.Add(formatter(state, exception));
            }
        }
    }
}
