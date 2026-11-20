using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Backroom.BuildingBlocks.Application.Organizations;
using Backroom.BuildingBlocks.Domain.Errors;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.BuildingBlocks.Presentation.Errors;
using Backroom.BuildingBlocks.Presentation.Persistence;
using Backroom.Testing.Postgres;

namespace Backroom.BuildingBlocks.IntegrationTests.Persistence;

[Collection(PostgresCollection.Name)]
public sealed class UnitOfWorkEndpointTests(PostgresDatabase database) : IAsyncLifetime
{
    private static readonly Guid s_organization = Guid.CreateVersion7();

    private WebApplication? _app;
    private HttpClient _client = null!;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await NotesSchema.PrepareAsync(database);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseTestServer();
        builder.Services.AddDatabaseSession().Configure(options =>
            options.ConnectionString = database.ApplicationConnectionString());
        builder.Services.AddModuleDbContext<NotesDbContext>(NotesSchema.Schema);

        var app = builder.Build();
        app.UseExceptionHandler(handler => handler.Run(context =>
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return Task.CompletedTask;
        }));

        // Stands in for the membership check, which sets the organization before the unit of work.
        var notes = app.MapGroup("/notes").AddEndpointFilter(static (context, next) =>
        {
            context.HttpContext.RequestServices.GetRequiredService<OrganizationContext>().Enter(s_organization);
            return next(context);
        }).WithUnitOfWork();

        notes.MapPost("/{text}", static async (string text, string? outcome, NotesDbContext db, CancellationToken cancellationToken) =>
        {
            db.Notes.Add(new Note { Id = Guid.CreateVersion7(), OrganizationId = s_organization, Text = text });
            await db.SaveChangesAsync(cancellationToken);

            return outcome switch
            {
                "conflict" => Error.Conflict("notes.duplicate", "Duplicate note.").ToProblem(),
                "rate-limited" => Error.RateLimited("notes.too_many", "Too many notes.", TimeSpan.FromSeconds(5)).ToProblem(),
                "throw" => throw new InvalidOperationException("Handler failed."),
                _ => TypedResults.Created(),
            };
        });

        // A typed union wraps the actual result (regression: it was committed as a success).
        notes.MapPost("/typed/{text}", static async Task<Results<Created, ProblemHttpResult>> (
            string text, bool fail, NotesDbContext db, CancellationToken cancellationToken) =>
        {
            db.Notes.Add(new Note { Id = Guid.CreateVersion7(), OrganizationId = s_organization, Text = text });
            await db.SaveChangesAsync(cancellationToken);
            return fail ? TypedResults.Problem(statusCode: StatusCodes.Status409Conflict) : TypedResults.Created();
        });

        await app.StartAsync(CancellationToken);
        _app = app;
        _client = app.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    [Fact]
    public async Task Request_WhenSuccessful_CommitsAsync()
    {
        using var response = await _client.PostAsync(new Uri("/notes/kept", UriKind.Relative), null, CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        (await NotesSchema.ReadAllAsync(database)).Should().Equal((s_organization, "kept"));
    }

    [Theory]
    [InlineData("conflict", HttpStatusCode.Conflict)]
    [InlineData("rate-limited", HttpStatusCode.TooManyRequests)]
    [InlineData("throw", HttpStatusCode.InternalServerError)]
    public async Task Request_WhenItFails_RollsBackAsync(string outcome, HttpStatusCode status)
    {
        using var response = await _client.PostAsync(new Uri($"/notes/discarded?outcome={outcome}", UriKind.Relative), null, CancellationToken);

        response.StatusCode.Should().Be(status);
        (await NotesSchema.ReadAllAsync(database)).Should().BeEmpty();
    }

    [Theory]
    [InlineData(false, HttpStatusCode.Created, 1)]
    [InlineData(true, HttpStatusCode.Conflict, 0)]
    public async Task TypedResultUnion_WhenReturned_UsesTheInnerStatusAsync(bool fail, HttpStatusCode status, int rows)
    {
        using var response = await _client.PostAsync(new Uri($"/notes/typed/note?fail={fail}", UriKind.Relative), null, CancellationToken);

        response.StatusCode.Should().Be(status);
        (await NotesSchema.ReadAllAsync(database)).Should().HaveCount(rows);
    }
}
