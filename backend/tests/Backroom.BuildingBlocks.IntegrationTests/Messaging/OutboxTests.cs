using System.Diagnostics;
using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Backroom.BuildingBlocks.Application.Messaging;
using Backroom.BuildingBlocks.Application.Organizations;
using Backroom.BuildingBlocks.Application.Persistence;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.BuildingBlocks.IntegrationTests.Persistence;
using Backroom.Testing.Postgres;

namespace Backroom.BuildingBlocks.IntegrationTests.Messaging;

[Collection(PostgresCollection.Name)]
public sealed class OutboxTests(PostgresDatabase database) : IAsyncLifetime
{
    private static readonly Guid s_organization = Guid.CreateVersion7();

    private readonly ServiceProvider _services = NotesSchema.CreateServices(database);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await NotesSchema.PrepareAsync(database);

    public ValueTask DisposeAsync() => _services.DisposeAsync();

    [Fact]
    public async Task AddAsync_WhenCommitted_StoresTheEventWithTraceContextAsync()
    {
        var noteAdded = NoteAdded.For(s_organization);
        using var activity = new Activity("test request").Start();

        await using (var scope = await BeginAsync())
        {
            await scope.ServiceProvider.GetRequiredService<IOutbox>().AddAsync(noteAdded, CancellationToken);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().CommitAsync(CancellationToken);
        }

        var rows = await OutboxRows.ReadAsync(database, CancellationToken);
        var row = rows.Should().ContainSingle().Subject;
        row.Id.Should().Be(noteAdded.EventId);
        row.OrgId.Should().Be(s_organization);
        row.EventType.Should().Be("rls_test.note-added.v1");
        row.TraceParent.Should().Be(activity.Id);
        row.PublishedAt.Should().BeNull();
        Encoding.UTF8.GetString(row.Payload.Span).Should().Contain("\"org_id\":").And.Contain("\"note_id\":").And.Contain("\"event_id\":");
    }

    [Fact]
    public async Task AddAsync_WhenRolledBack_StoresNothingAsync()
    {
        await using (var scope = await BeginAsync())
        {
            await scope.ServiceProvider.GetRequiredService<IOutbox>().AddAsync(NoteAdded.For(s_organization), CancellationToken);
        }

        (await OutboxRows.ReadAsync(database, CancellationToken)).Should().BeEmpty();
    }

    [Fact]
    public async Task AddAsync_ForAnotherOrganization_ThrowsAsync()
    {
        await using var scope = await BeginAsync();

        var add = () => scope.ServiceProvider.GetRequiredService<IOutbox>().AddAsync(NoteAdded.For(Guid.CreateVersion7()), CancellationToken);

        await add.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task AddAsync_WhenPayloadIsTooLarge_ThrowsAsync()
    {
        await using var scope = await BeginAsync();
        var large = new LargeNoteAdded(Guid.CreateVersion7(), s_organization, DateTimeOffset.UtcNow, new string('x', 70_000));

        var add = () => scope.ServiceProvider.GetRequiredService<IOutbox>().AddAsync(large, CancellationToken);

        await add.Should().ThrowAsync<ArgumentException>().WithMessage("*limit*");
    }

    [Theory]
    [InlineData("SELECT count(*) FROM rls_test.outbox")]
    [InlineData("UPDATE rls_test.outbox SET published_at = now()")]
    [InlineData("DELETE FROM rls_test.outbox")]
    public async Task ApplicationRole_ReadingOrChangingTheOutbox_IsRejectedAsync(string sql)
    {
        await using var scope = await BeginAsync();
        var session = scope.ServiceProvider.GetRequiredService<DatabaseSession>();
#pragma warning disable CA2100 // Test SQL from InlineData.
        await using var command = new NpgsqlCommand(sql, session.Connection, session.Transaction);
#pragma warning restore CA2100

        var run = () => command.ExecuteNonQueryAsync(CancellationToken);

        // 42501: permission denied; the application role may only insert.
        (await run.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501");
    }

    private async Task<AsyncServiceScope> BeginAsync()
    {
        var scope = _services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<OrganizationContext>().Enter(s_organization);
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(CancellationToken);
        return scope;
    }
}
