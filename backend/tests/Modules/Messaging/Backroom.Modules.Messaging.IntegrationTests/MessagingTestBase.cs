using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Backroom.BuildingBlocks.Application.Organizations;
using Backroom.BuildingBlocks.Application.Persistence;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.BuildingBlocks.Presentation.Authentication;
using Backroom.Modules.Messaging.Infrastructure;
using Backroom.Modules.Messaging.Presentation;
using Backroom.Modules.Organization.Contracts;
using Backroom.Modules.Organization.Infrastructure;
using Backroom.Modules.Permissions.Contracts;
using Backroom.Modules.Permissions.Infrastructure;
using Backroom.Modules.Realtime.Contracts;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;

namespace Backroom.Modules.Messaging.IntegrationTests;

/// <summary>
/// A cell with Messaging, Organization and Permissions on a test server, with real tokens, real PostgreSQL and
/// real row-level security. The live event publisher is a recording one; the logs are captured.
/// </summary>
public abstract class MessagingTestBase(PostgresDatabase database, KeycloakRealm keycloak) : IAsyncLifetime
{
    protected const string Alice = "alice@example.test";
    protected const string Bob = "bob@example.test";

    private readonly Dictionary<string, string> _tokens = [];
    private WebApplication _cell = null!;
    private HttpClient _client = null!;
    private string _cellId = null!;

    protected static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    internal RecordingLiveEventPublisher Publisher { get; } = new();

    internal CapturingLoggerProvider Logs { get; } = new();

    internal ScriptedIdGenerator IdScript { get; } = new();

    internal TestCellEpoch CellEpoch { get; } = new();

    /// <summary><c>Features:MessageSync</c> for the cell of this test class.</summary>
    protected virtual bool MessageSyncEnabled => false;

    /// <summary><c>Features:ConversationMemberRemoval</c> for the cell of this test class.</summary>
    protected virtual bool MemberRemovalEnabled => true;

    /// <summary><c>Features:RichText</c> for the cell of this test class.</summary>
    protected virtual bool RichTextEnabled => false;

    /// <summary><c>Features:MessageEdit</c> for the cell of this test class.</summary>
    protected virtual bool MessageEditEnabled => false;

    /// <summary><c>Features:MessageReply</c> for the cell of this test class.</summary>
    protected virtual bool MessageReplyEnabled => false;

    /// <summary><c>Features:MessageReactions</c> for the cell of this test class.</summary>
    protected virtual bool MessageReactionsEnabled => false;

    protected PostgresDatabase Database => database;

    protected IServiceProvider Services => _cell.Services;

    public async ValueTask InitializeAsync()
    {
        await MessagingSchema.MigrateOnceAsync(database, CancellationToken);
        _cellId = await keycloak.CreateCellScopeAsync();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        builder.Logging.AddProvider(Logs);
        builder.Configuration["Authentication:Authority"] = keycloak.Url;
        builder.Configuration["Database:ConnectionString"] = database.ApplicationConnectionString();
        builder.AddAccessTokenAuthentication(Audiences.Cell(_cellId), [KeycloakClients.Web, KeycloakClients.Desktop]);
        builder.Services.AddDatabaseSession().BindConfiguration(DatabaseOptions.SectionName);
        builder.Services.AddPermissions();
        builder.Services.AddOrganization();
        builder.Services.AddSingleton<ILiveEventPublisher>(Publisher);
        builder.Services.AddSingleton<Backroom.BuildingBlocks.Application.Cells.ICellEpoch>(CellEpoch);
        builder.Configuration["Features:MessageSync"] = MessageSyncEnabled ? "true" : "false";
        builder.Configuration["Features:ConversationMemberRemoval"] = MemberRemovalEnabled ? "true" : "false";
        builder.Configuration["Features:RichText"] = RichTextEnabled ? "true" : "false";
        builder.Configuration["Features:MessageEdit"] = MessageEditEnabled ? "true" : "false";
        builder.Configuration["Features:MessageReply"] = MessageReplyEnabled ? "true" : "false";
        builder.Configuration["Features:MessageReactions"] = MessageReactionsEnabled ? "true" : "false";
        builder.Services.AddSingleton<Backroom.BuildingBlocks.Application.Identifiers.IIdGenerator>(IdScript);
        builder.Services.AddMessaging();
        builder.Services.AddMessagingEndpoints();
        // Development would throw on a bad query string before the filters; production answers through them.
        builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);

        _cell = builder.Build();
        _cell.UseAuthentication();
        _cell.UseAuthorization();
        _cell.UseRateLimiter();
        _cell.MapMessagingEndpoints();
        await _cell.StartAsync(CancellationToken);
        _client = _cell.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _cell.DisposeAsync();
    }

    protected async Task<Guid> AccountAsync(string username) => Guid.Parse(await keycloak.UserIdAsync(username));

    protected async Task<HttpResponseMessage> GetAsync(string path, string? username) => await SendAsync(HttpMethod.Get, path, username, null);

    protected async Task<HttpResponseMessage> PostAsync(string path, string? username, object body) => await SendAsync(HttpMethod.Post, path, username, body);

    protected async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? username, object? body)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (username is not null)
        {
            if (!_tokens.TryGetValue(username, out var token))
            {
                token = (await keycloak.SignInAsync(username, $"openid cell:{_cellId}")).AccessToken;
                _tokens[username] = token;
            }

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await _client.SendAsync(request, CancellationToken);
    }

    protected static async Task<JsonNode> ReadJsonAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync(CancellationToken))!;

    protected async Task<Guid> CreateChannelAsync(Guid organizationId, string username, string name)
    {
        using var response = await PostAsync($"/api/v1/orgs/{organizationId}/channels", username, new { name });
        response.EnsureSuccessStatusCode();
        return (await ReadJsonAsync(response))["id"]!.GetValue<Guid>();
    }

    /// <summary>A private channel through the API; the creator becomes its owner.</summary>
    protected async Task<Guid> CreatePrivateChannelAsync(Guid organizationId, string username, string name, params Guid[] memberAccountIds)
    {
        using var response = await PostAsync($"/api/v1/orgs/{organizationId}/channels", username, new { name, @private = true, memberAccountIds });
        response.EnsureSuccessStatusCode();
        return (await ReadJsonAsync(response))["id"]!.GetValue<Guid>();
    }

    /// <summary>An active member of the organization who never signs in.</summary>
    protected async Task<Guid> AddSilentMemberAsync(Guid organizationId)
    {
        var account = Guid.CreateVersion7();
        await ExecuteAsync(
            """
            INSERT INTO organization.members (org_id, account_id, display_name, email, locale, status, version, joined_at)
            VALUES ($1, $2, 'Silent', $3, 'en-US', 'active', 1, now())
            """,
            organizationId, account, $"{account:N}@example.test");
        return account;
    }

    protected static async Task<string?> ErrorCodeAsync(HttpResponseMessage response) =>
        (await ReadJsonAsync(response))["code"]?.GetValue<string>();

    protected async Task<JsonNode> SendMessageAsync(Guid organizationId, Guid channelId, string username, string text, Guid? clientMessageId = null)
    {
        using var response = await PostAsync(
            $"/api/v1/orgs/{organizationId}/conversations/{channelId}/messages", username, new { clientMessageId = clientMessageId ?? Guid.NewGuid(), text });
        response.EnsureSuccessStatusCode();
        return await ReadJsonAsync(response);
    }

    /// <summary>
    /// Deletes the message the way the delete endpoint will (docs/contracts/phase-2.md, step 9): the conversation's
    /// next change number becomes the message's <c>version_seq</c>, and the content is erased, leaving the tombstone.
    /// </summary>
    protected Task DeleteAsync(Guid conversationId, Guid messageId) => ExecuteAsync(
        """
        WITH change AS (UPDATE messaging.conversations SET change_seq = change_seq + 1 WHERE id = $1 RETURNING change_seq)
        UPDATE messaging.messages m SET version_seq = change.change_seq, deleted_at = now(), text = NULL, blocks = NULL
        FROM change WHERE m.id = $2
        """,
        conversationId, messageId);

    /// <summary>Edits the message the way the edit endpoint will: the next change number becomes its <c>version_seq</c>.</summary>
    protected Task EditAsync(Guid conversationId, Guid messageId, string text) => ExecuteAsync(
        """
        WITH change AS (UPDATE messaging.conversations SET change_seq = change_seq + 1 WHERE id = $1 RETURNING change_seq)
        UPDATE messaging.messages m SET version_seq = change.change_seq, edited_at = now(), text = $3
        FROM change WHERE m.id = $2
        """,
        conversationId, messageId, text);

    /// <summary>An organization of its own for each test, with the account as its owner, as the control plane's message creates it.</summary>
    protected async Task<Guid> ProvisionForAsync(string username)
    {
        var organizationId = Guid.CreateVersion7();
        var owner = await AccountAsync(username);
        await InOrganizationAsync(organizationId, async services =>
        {
            var result = await services.GetRequiredService<IOrganizationProvisioning>().ProvisionAsync(
                new NewOrganization(organizationId, "Acme", owner, username, username, "en-US"), CancellationToken);
            result.IsSuccess.Should().BeTrue();
        });
        return organizationId;
    }

    /// <summary>Adds the account as an active member; with <paramref name="role"/> <see langword="null"/> it holds no role, so no permission.</summary>
    protected async Task AddMemberAsync(Guid organizationId, string username, string? role)
    {
        var account = await AccountAsync(username);
        await ExecuteAsync(
            """
            INSERT INTO organization.members (org_id, account_id, display_name, email, locale, status, version, joined_at)
            VALUES ($1, $2, 'Someone', $3, 'en-US', 'active', 1, now())
            """,
            organizationId, account, username);
        if (role is not null)
        {
            await InOrganizationAsync(organizationId, services =>
                services.GetRequiredService<IPermissionsProvisioning>().AssignRoleAsync(organizationId, account, role, CancellationToken));
        }
    }

    [SuppressMessage("Security", "CA2100", Justification = "The SQL comes from the tests; values are parameters.")]
    protected async Task ExecuteAsync(string sql, params object[] parameters)
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

    [SuppressMessage("Security", "CA2100", Justification = "The SQL comes from the tests; values are parameters.")]
    protected async Task<object?[]> QuerySingleAsync(string sql, params object[] parameters)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        }

        await using var reader = await command.ExecuteReaderAsync(CancellationToken);
        (await reader.ReadAsync(CancellationToken)).Should().BeTrue("the query should return a row");
        var values = new object?[reader.FieldCount];
        reader.GetValues(values!);
        return values;
    }

    protected async Task<IReadOnlyList<(string Type, JsonNode Payload)>> OutboxAsync(Guid organizationId)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand("SELECT event_type, payload FROM messaging.outbox WHERE org_id = $1 ORDER BY id", connection);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organizationId });
        await using var reader = await command.ExecuteReaderAsync(CancellationToken);
        var events = new List<(string, JsonNode)>();
        while (await reader.ReadAsync(CancellationToken))
        {
            events.Add((reader.GetString(0), JsonNode.Parse(Encoding.UTF8.GetString(reader.GetFieldValue<byte[]>(1)))!));
        }

        return events;
    }

    protected async Task InOrganizationAsync(Guid organizationId, Func<IServiceProvider, Task> work)
    {
        await using var scope = _cell.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<OrganizationContext>().Enter(organizationId);
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await unitOfWork.BeginAsync(CancellationToken);
        await work(scope.ServiceProvider);
        await unitOfWork.CommitAsync(CancellationToken);
    }
}
