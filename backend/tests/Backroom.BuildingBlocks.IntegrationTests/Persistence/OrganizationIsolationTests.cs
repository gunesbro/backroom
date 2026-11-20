using System.Diagnostics.CodeAnalysis;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Backroom.BuildingBlocks.Application.Organizations;
using Backroom.BuildingBlocks.Application.Persistence;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.Testing.Postgres;

namespace Backroom.BuildingBlocks.IntegrationTests.Persistence;

/// <summary>
/// Privacy rule: every row carries org_id and RLS is on; a request sees only its organization's rows.
/// </summary>
[Trait("Category", "Privacy")]
[Collection(PostgresCollection.Name)]
public sealed class OrganizationIsolationTests(PostgresDatabase database) : IAsyncLifetime
{
    private const string RowSecurityViolation = "42501";

    private static readonly Guid s_organizationA = Guid.CreateVersion7();
    private static readonly Guid s_organizationB = Guid.CreateVersion7();

    private readonly ServiceProvider _services = NotesSchema.CreateServices(database);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await NotesSchema.PrepareAsync(database);
        await NotesSchema.SeedAsync(database, s_organizationA, "a1");
        await NotesSchema.SeedAsync(database, s_organizationA, "a2");
        await NotesSchema.SeedAsync(database, s_organizationB, "b1");
    }

    public ValueTask DisposeAsync() => _services.DisposeAsync();

    [Fact]
    public async Task Read_WhenInOrganization_ReturnsOnlyItsRowsAsync()
    {
        await using var scope = await BeginAsync(s_organizationA);

        var texts = await scope.Notes.Notes.OrderBy(n => n.Text).Select(n => n.Text).ToListAsync(CancellationToken);

        texts.Should().Equal("a1", "a2");
    }

    [Fact]
    public async Task Read_WithoutOrganization_ReturnsNoRowsAsync()
    {
        await using var scope = await BeginAsync(organizationId: null);

        var count = await scope.Notes.Notes.CountAsync(CancellationToken);

        count.Should().Be(0);
    }

    [Fact]
    public async Task Read_WhenFilteringForAnotherOrganization_ReturnsNoRowsAsync()
    {
        await using var scope = await BeginAsync(s_organizationA);

        var count = await scope.Notes.Notes.CountAsync(n => n.OrganizationId == s_organizationB, CancellationToken);

        count.Should().Be(0);
    }

    [Fact]
    public async Task Insert_ForAnotherOrganization_IsRejectedAsync()
    {
        await using var scope = await BeginAsync(s_organizationA);
        scope.Notes.Notes.Add(new Note { Id = Guid.CreateVersion7(), OrganizationId = s_organizationB, Text = "intruder" });

        var save = () => scope.Notes.SaveChangesAsync(CancellationToken);

        (await save.Should().ThrowAsync<DbUpdateException>())
            .WithInnerException<PostgresException>().Which.SqlState.Should().Be(RowSecurityViolation);
    }

    [Fact]
    public async Task Update_MovingRowToAnotherOrganization_IsRejectedAsync()
    {
        await using var scope = await BeginAsync(s_organizationA);

        var move = () => scope.Notes.Notes.ExecuteUpdateAsync(
            setters => setters.SetProperty(n => n.OrganizationId, s_organizationB), CancellationToken);

        (await move.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(RowSecurityViolation);
    }

    [Fact]
    public async Task UpdateAndDelete_OfAnotherOrganizationsRows_AffectNothingAsync()
    {
        await using (var scope = await BeginAsync(s_organizationA))
        {
            var updated = await scope.Notes.Notes.Where(n => n.OrganizationId == s_organizationB)
                .ExecuteUpdateAsync(setters => setters.SetProperty(n => n.Text, "changed"), CancellationToken);
            var deleted = await scope.Notes.Notes.Where(n => n.OrganizationId == s_organizationB)
                .ExecuteDeleteAsync(CancellationToken);
            await scope.UnitOfWork.CommitAsync(CancellationToken);

            updated.Should().Be(0);
            deleted.Should().Be(0);
        }

        (await NotesSchema.ReadAllAsync(database)).Should().Contain((s_organizationB, "b1"));
    }

    [Fact]
    public async Task PooledConnection_AfterOrganizationTransaction_DoesNotKeepTheOrganizationAsync()
    {
        // One pooled connection, so the second unit of work reuses the first one's connection.
        await using var services = NotesSchema.CreateServices(database, maxPoolSize: 1);

        await using (var first = await BeginAsync(s_organizationA, services))
        {
            (await first.Notes.Notes.CountAsync(CancellationToken)).Should().Be(2);
            await first.UnitOfWork.CommitAsync(CancellationToken);
        }

        await using (var rolledBack = await BeginAsync(s_organizationB, services))
        {
            (await rolledBack.Notes.Notes.CountAsync(CancellationToken)).Should().Be(1);
            await rolledBack.UnitOfWork.RollbackAsync(CancellationToken);
        }

        // Regression: the setting now reads as '' on this connection; the policy must treat it as
        // "no organization" instead of failing the uuid cast.
        await using var next = await BeginAsync(organizationId: null, services);
        (await next.Notes.Notes.CountAsync(CancellationToken)).Should().Be(0);
    }

    [Fact]
    public async Task ApplicationRole_WithRowSecurityOff_CannotReadAsync()
    {
        await using var scope = await BeginAsync(s_organizationA);

        var read = () => ExecuteAsync(scope, $"SET LOCAL row_security = off; SELECT count(*) FROM {NotesSchema.Schema}.{NotesSchema.Table}");

        (await read.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(RowSecurityViolation);
    }

    [Theory]
    [InlineData("ALTER TABLE rls_test.notes DISABLE ROW LEVEL SECURITY")]
    [InlineData("ALTER TABLE rls_test.notes NO FORCE ROW LEVEL SECURITY")]
    [InlineData("DROP POLICY org_isolation ON rls_test.notes")]
    [InlineData("CREATE POLICY open ON rls_test.notes USING (true)")]
    public async Task ApplicationRole_ChangingRowSecurity_IsRejectedAsync(string sql)
    {
        await using var scope = await BeginAsync(s_organizationA);

        var change = () => ExecuteAsync(scope, sql);

        // 42501: must be owner of table.
        (await change.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(RowSecurityViolation);
    }

    [Theory]
    [InlineData("superuser")]
    [InlineData("owner")]
    [InlineData("member-of-owner")]
    [InlineData("bypassrls")]
    [InlineData("member-of-bypassrls")]
    public async Task Begin_WithRoleThatCanSwitchRowSecurityOff_ThrowsAsync(string kind)
    {
        var suffix = Guid.CreateVersion7().ToString("N")[^12..];
        var connectionString = kind switch
        {
            "superuser" => database.SuperuserConnectionString,
            "owner" => database.MigratorConnectionString,
            "member-of-owner" => await database.CreateLoginRoleAsync($"owner_member_{suffix}", "", PostgresDatabase.MigratorRole),
            "bypassrls" => await database.CreateLoginRoleAsync($"bypass_{suffix}", "BYPASSRLS"),
            _ => await CreateMemberOfBypassRoleAsync(suffix),
        };
        await using var services = NotesSchema.CreateServices(database, connectionString: connectionString);
        await using var scope = services.CreateAsyncScope();

        var begin = () => scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(CancellationToken);

        await begin.Should().ThrowAsync<InvalidOperationException>().WithMessage("*bypasses row-level security or owns*");
    }

    [Fact]
    public async Task Query_WhenConnectionIsLost_ThrowsInsteadOfReconnectingAsync()
    {
        await using var scope = await BeginAsync(s_organizationA);
        var processId = scope.Services.GetRequiredService<DatabaseSession>().Connection.ProcessID;
        await database.ExecuteAsSuperuserAsync($"SELECT pg_terminate_backend({processId})");

        var query = () => scope.Notes.Notes.CountAsync(CancellationToken);

        await query.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task ConcurrentUnitsOfWork_OnASmallPool_EachSeeOnlyTheirOrganizationAsync()
    {
        await using var services = NotesSchema.CreateServices(database, maxPoolSize: 2);

        var counts = await Task.WhenAll(Enumerable.Range(0, 40).Select(async i =>
        {
            var organizationId = i % 2 == 0 ? s_organizationA : s_organizationB;
            await using var scope = await BeginAsync(organizationId, services);
            var texts = await scope.Notes.Notes.Select(n => n.Text).ToListAsync(CancellationToken);
            await scope.UnitOfWork.CommitAsync(CancellationToken);
            return (organizationId, texts);
        }));

        counts.Where(c => c.organizationId == s_organizationA).Should().OnlyContain(c => c.texts.Count == 2 && c.texts.All(t => t.StartsWith('a')));
        counts.Where(c => c.organizationId == s_organizationB).Should().OnlyContain(c => c.texts.Count == 1 && c.texts[0] == "b1");
    }

    private async Task<string> CreateMemberOfBypassRoleAsync(string suffix)
    {
        await database.ExecuteAsSuperuserAsync($"CREATE ROLE bypass_group_{suffix} NOLOGIN BYPASSRLS");
        return await database.CreateLoginRoleAsync($"bypass_member_{suffix}", "", $"bypass_group_{suffix}");
    }

    [Fact]
    public async Task Query_WhenOrganizationChangesAfterBegin_ThrowsAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(CancellationToken);
        scope.ServiceProvider.GetRequiredService<OrganizationContext>().Enter(s_organizationA);
        var notes = scope.ServiceProvider.GetRequiredService<NotesDbContext>();

        var query = () => notes.Notes.CountAsync(CancellationToken);

        await query.Should().ThrowAsync<InvalidOperationException>().WithMessage("*organization context changed*");
    }

    [Fact]
    public async Task Query_BeforeBegin_ThrowsAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        var notes = scope.ServiceProvider.GetRequiredService<NotesDbContext>();

        var query = () => notes.Notes.CountAsync(CancellationToken);

        await query.Should().ThrowAsync<InvalidOperationException>().WithMessage("*has not begun*");
    }

    [Fact]
    public async Task Query_AfterCommit_ThrowsAsync()
    {
        await using var scope = await BeginAsync(s_organizationA);
        await scope.UnitOfWork.CommitAsync(CancellationToken);

        var query = () => scope.Notes.Notes.CountAsync(CancellationToken);

        await query.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already completed*");
    }

    [Fact]
    public async Task DbContext_WhenCreatedBeforeBegin_WorksInTheUnitOfWorkAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        var notes = scope.ServiceProvider.GetRequiredService<NotesDbContext>();
        scope.ServiceProvider.GetRequiredService<OrganizationContext>().Enter(s_organizationA);
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(CancellationToken);

        var count = await notes.Notes.CountAsync(CancellationToken);

        count.Should().Be(2);
    }

    [Fact]
    public async Task Begin_WhenAlreadyBegun_ThrowsAsync()
    {
        await using var scope = await BeginAsync(s_organizationA);

        var begin = () => scope.UnitOfWork.BeginAsync(CancellationToken);

        await begin.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Commit_WhenCalled_PersistsChangesAsync()
    {
        await using (var scope = await BeginAsync(s_organizationA))
        {
            scope.Notes.Notes.Add(new Note { Id = Guid.CreateVersion7(), OrganizationId = s_organizationA, Text = "a3" });
            await scope.Notes.SaveChangesAsync(CancellationToken);
            await scope.UnitOfWork.CommitAsync(CancellationToken);
        }

        (await NotesSchema.ReadAllAsync(database)).Should().Contain((s_organizationA, "a3"));
    }

    [Fact]
    public async Task Dispose_WithoutCommit_RollsBackAsync()
    {
        await using (var scope = await BeginAsync(s_organizationA))
        {
            scope.Notes.Notes.Add(new Note { Id = Guid.CreateVersion7(), OrganizationId = s_organizationA, Text = "a3" });
            await scope.Notes.SaveChangesAsync(CancellationToken);
        }

        (await NotesSchema.ReadAllAsync(database)).Should().NotContain((s_organizationA, "a3"));
    }

    [Fact]
    public async Task Audit_WhenTableHasThePolicy_ReportsNothingAsync()
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(CancellationToken);

        var violations = await RowLevelSecurityAudit.FindViolationsAsync(
            connection, [NotesSchema.Schema], new HashSet<string>(StringComparer.Ordinal), CancellationToken);

        violations.Should().BeEmpty();
    }

    [SuppressMessage("Security", "CA2100", Justification = "Test helper; the SQL is written in the tests.")]
    private static async Task ExecuteAsync(SessionScope scope, string sql)
    {
        var session = scope.Services.GetRequiredService<DatabaseSession>();
        await using var command = new NpgsqlCommand(sql, session.Connection, session.Transaction);
        await command.ExecuteNonQueryAsync(CancellationToken);
    }

    private Task<SessionScope> BeginAsync(Guid? organizationId) => BeginAsync(organizationId, _services);

    private static async Task<SessionScope> BeginAsync(Guid? organizationId, ServiceProvider services)
    {
        var scope = services.CreateAsyncScope();
        if (organizationId is { } id)
        {
            scope.ServiceProvider.GetRequiredService<OrganizationContext>().Enter(id);
        }

        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await unitOfWork.BeginAsync(CancellationToken);
        return new SessionScope(scope, unitOfWork, scope.ServiceProvider.GetRequiredService<NotesDbContext>());
    }

    private sealed class SessionScope(AsyncServiceScope scope, IUnitOfWork unitOfWork, NotesDbContext notes) : IAsyncDisposable
    {
        public IServiceProvider Services => scope.ServiceProvider;

        public IUnitOfWork UnitOfWork => unitOfWork;

        public NotesDbContext Notes => notes;

        public ValueTask DisposeAsync() => scope.DisposeAsync();
    }
}
