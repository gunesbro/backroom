using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Backroom.Modules.Organization.Infrastructure.Invitations;
using Backroom.Testing;
using Backroom.Testing.Postgres;

namespace Backroom.Modules.Organization.IntegrationTests;

/// <summary>
/// The cell's scheduled invitation jobs (docs/contracts/phase-1.md, flow 3, step 8, and "Privacy and retention")
/// against real PostgreSQL, run as the relay role like the worker. The clock is a fake one in 2001: the jobs only
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

        var expired = await Jobs().ExpireAsync(CancellationToken);

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

        var expired = await Jobs().ExpireAsync(CancellationToken);

        expired.Should().Be(2);
        var row = await RowAsync(due);
        row.Status.Should().Be("expired");
        row.ExpiredAt.Should().Be(s_now);
        row.Email.Should().NotBeNull("the email is kept until the retention ends");
        (await RowAsync(dueExactly)).Status.Should().Be("expired");
        (await RowAsync(notDue)).Status.Should().Be("pending");
        (await RowAsync(accepted)).Should().Match<(string Status, DateTimeOffset? ExpiredAt, string? Email)>(r => r.Status == "accepted" && r.ExpiredAt == null && r.Email != null, "accepted invitations are untouched");
        (await RowAsync(revoked)).Status.Should().Be("revoked");
        (await Jobs().ExpireAsync(CancellationToken)).Should().Be(0, "a second run finds nothing more");
    }

    [Fact]
    public async Task Expire_PublishesNoEventAndLogsIdsOnlyAsync()
    {
        var invitation = await AddAsync("pending", expiresAt: s_now.AddDays(-2));
        var logger = new CapturingLogger<InvitationJobs>();

        await Jobs(logger: logger).ExpireAsync(CancellationToken);

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

        var first = await Jobs().ExpireAsync(batchSize: 3, maxBatches: 2, CancellationToken);

        first.Should().Be(6);
        foreach (var invitation in invitations[..6])
        {
            (await RowAsync(invitation)).Status.Should().Be("expired");
        }

        (await RowAsync(invitations[6])).Status.Should().Be("pending", "the newest expiry waits for the next run");
        (await Jobs().ExpireAsync(batchSize: 3, maxBatches: 2, CancellationToken)).Should().Be(1);
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
        var expired = await Jobs().ExpireAsync(timeout.Token);
        await transaction.RollbackAsync(CancellationToken);

        expired.Should().Be(1, "the batch skipped the row another transaction holds");
        (await RowAsync(other)).Status.Should().Be("expired");
        (await RowAsync(locked)).Status.Should().Be("pending", "the next run picks it up");
        (await Jobs().ExpireAsync(CancellationToken)).Should().Be(1);
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
            Jobs().ExpireAsync(batchSize: 5, maxBatches: 20, CancellationToken),
            Jobs().ExpireAsync(batchSize: 5, maxBatches: 20, CancellationToken));

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

        var cleared = await Jobs().ApplyRetentionAsync(CancellationToken);

        cleared.Should().Be(2);
        (await RowAsync(expiredOld)).Should().Match<(string Status, DateTimeOffset? ExpiredAt, string? Email)>(r => r.Status == "expired" && r.Email == null);
        (await RowAsync(revokedOld)).Status.Should().Be("revoked");
        (await RowAsync(revokedOld)).Email.Should().BeNull();
        (await RowAsync(expiredYoung)).Email.Should().NotBeNull("30 days have not passed");
        (await RowAsync(revokedYoung)).Email.Should().NotBeNull("30 days have not passed");
        (await RowAsync(acceptedOld)).Email.Should().NotBeNull("accepted invitations are untouched");
        (await RowAsync(pendingOld)).Email.Should().NotBeNull("a pending invitation needs its email until it is marked expired");
        (await Jobs().ApplyRetentionAsync(CancellationToken)).Should().Be(0);

        _time.Advance(TimeSpan.FromSeconds(2));
        (await Jobs().ApplyRetentionAsync(CancellationToken)).Should().Be(1, "the young expired one is now 30 days old");
        (await RowAsync(expiredYoung)).Email.Should().BeNull();
    }

    [Fact]
    public async Task ApplyRetention_WithMoreDueThanTheRunAllows_StopsAtTheLimitAsync()
    {
        for (var index = 0; index < 5; index++)
        {
            await AddAsync("expired", expiresAt: s_now.AddDays(-60), expiredAt: s_now.AddDays(-40).AddHours(index));
        }

        (await Jobs().ApplyRetentionAsync(batchSize: 2, maxBatches: 2, CancellationToken)).Should().Be(4);
        (await Jobs().ApplyRetentionAsync(batchSize: 2, maxBatches: 2, CancellationToken)).Should().Be(1);
    }

    [Fact]
    public async Task Jobs_UnderTurkishCulture_BehaveTheSameAsync()
    {
        using var culture = CultureScope.Turkish();
        var due = await AddAsync("pending", expiresAt: s_now.AddDays(-2), email: "ISIK@example.test");
        var old = await AddAsync("revoked", expiresAt: s_now.AddDays(-60), revokedAt: s_now.AddDays(-40), email: "IIIK@example.test");

        (await Jobs().ExpireAsync(CancellationToken)).Should().Be(1);
        (await Jobs().ApplyRetentionAsync(CancellationToken)).Should().Be(1);

        (await RowAsync(due)).Status.Should().Be("expired");
        (await RowAsync(old)).Email.Should().BeNull();
    }

    [Fact]
    public async Task RelayRole_CannotReadTheCodeHashOrSaltAsync()
    {
        var invitation = await AddAsync("pending", expiresAt: s_now);
        await using var dataSource = NpgsqlDataSource.Create(database.RelayConnectionString);

        await using var command = dataSource.CreateCommand("SELECT code_hash FROM organization.invitations WHERE id = $1");
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = invitation });
        var read = async () => await command.ExecuteScalarAsync(CancellationToken);

        (await read.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Theory]
    [InlineData(0, 250, 40)]
    [InlineData(5, 250, 40)]
    [InlineData(300, 0, 40)]
    [InlineData(300, 1001, 40)]
    [InlineData(300, 250, 0)]
    [InlineData(300, 250, 201)]
    [InlineData(7200, 250, 40)]
    public void Validate_WithValuesOutsideTheLimits_FailsAtStartup(int intervalSeconds, int batchSize, int maxBatches)
    {
        var options = new InvitationJobsOptions
        {
            Interval = TimeSpan.FromSeconds(intervalSeconds),
            BatchSize = batchSize,
            MaxBatchesPerRun = maxBatches,
            RelayConnectionString = "Host=db",
        };

        new InvitationJobsOptionsValidator().Validate(null, options).Failed.Should().BeTrue();
    }

    [Fact]
    public void Validate_WithDefaultsAndTheRelayConnection_Succeeds()
    {
        new InvitationJobsOptionsValidator().Validate(null, new InvitationJobsOptions { RelayConnectionString = "Host=db" }).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_WithoutTheRelayConnection_Fails()
    {
        new InvitationJobsOptionsValidator().Validate(null, new InvitationJobsOptions()).Failed.Should().BeTrue();
    }

    private InvitationJobs Jobs(ILogger<InvitationJobs>? logger = null)
    {
        var dataSource = NpgsqlDataSource.Create(database.RelayConnectionString);
        _dataSources.Add(dataSource);
        return new InvitationJobs(dataSource, Options.Create(new InvitationJobsOptions { RelayConnectionString = "unused" }), _time, logger ?? NullLogger<InvitationJobs>.Instance);
    }

    private async Task<Guid> AddAsync(string status, DateTimeOffset expiresAt, DateTimeOffset? expiredAt = null, DateTimeOffset? revokedAt = null, string? email = null)
    {
        var invitation = Guid.CreateVersion7();
        await ExecuteAsync(
            """
            INSERT INTO organization.invitations
                (id, org_id, email, role, salt, key_version, code_version, code_hash, status, created_by, created_at, expires_at, expired_at, revoked_at)
            VALUES ($1, $2, $3, 'member', $4, 1, 1, $5, $6, $7, $8, $9, $10, $11)
            """,
            invitation, _organization, email ?? $"{status}-{invitation:N}@example.test", RandomNumberGenerator.GetBytes(16), RandomNumberGenerator.GetBytes(32), status,
            Guid.CreateVersion7(), expiresAt.AddDays(-7), expiresAt, (object?)expiredAt ?? DBNull.Value, (object?)revokedAt ?? DBNull.Value);
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
