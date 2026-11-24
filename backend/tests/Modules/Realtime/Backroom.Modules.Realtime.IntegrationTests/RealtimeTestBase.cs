using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Backroom.BuildingBlocks.Application.Organizations;
using Backroom.BuildingBlocks.Application.Persistence;
using Backroom.Modules.Organization.Contracts;
using Backroom.Modules.Permissions.Contracts;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;
using Backroom.Testing.Valkey;

namespace Backroom.Modules.Realtime.IntegrationTests;

/// <summary>
/// A cell with two app nodes (A and B) that share the database and one Valkey, with real tokens, real row-level
/// security and a real backplane. Each test gets its own organization.
/// </summary>
public abstract class RealtimeTestBase(PostgresDatabase database, KeycloakRealm keycloak, ValkeyServer valkey) : IAsyncLifetime
{
    protected const string Alice = "alice@example.test";
    protected const string Bob = "bob@example.test";

    private readonly Dictionary<string, string> _tokens = [];
    private readonly List<IAsyncDisposable> _disposables = [];

    protected static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    protected string CellId { get; private set; } = null!;

    internal CellNode NodeA { get; private set; } = null!;

    internal CellNode NodeB { get; private set; } = null!;

    protected PostgresDatabase Database => database;

    protected KeycloakRealm Keycloak => keycloak;

    protected ValkeyServer Valkey => valkey;

    /// <summary>Extra configuration of both nodes (a test's limits).</summary>
    protected virtual IReadOnlyDictionary<string, string?>? Settings => null;

    public async ValueTask InitializeAsync()
    {
        await RealtimeSchema.MigrateOnceAsync(database, CancellationToken);
        CellId = await keycloak.CreateCellScopeAsync();
        NodeA = await CellNode.StartAsync(database, keycloak.Url, CellId, valkey.ConnectionString, Settings, cancellationToken: CancellationToken);
        NodeB = await CellNode.StartAsync(database, keycloak.Url, CellId, valkey.ConnectionString, Settings, cancellationToken: CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var disposable in _disposables)
        {
            await disposable.DisposeAsync();
        }

        await NodeA.DisposeAsync();
        await NodeB.DisposeAsync();
    }

    protected async Task<Guid> AccountAsync(string username) => Guid.Parse(await keycloak.UserIdAsync(username));

    protected async Task<string> TokenAsync(string username)
    {
        if (!_tokens.TryGetValue(username, out var token))
        {
            token = (await keycloak.SignInAsync(username, $"openid cell:{CellId}")).AccessToken;
            _tokens[username] = token;
        }

        return token;
    }

    /// <summary>A connected hub client of <paramref name="username"/> on <paramref name="node"/>; disposed with the test.</summary>
    internal async Task<HubClient> ConnectAsync(CellNode node, string username)
    {
        var client = HubClient.Create(node, await TokenAsync(username));
        _disposables.Add(client);
        await client.StartAsync();
        return client;
    }

    internal async Task<HttpResponseMessage> PostAsync(CellNode node, string path, string username, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative)) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(username));
        return await node.Client.SendAsync(request, CancellationToken);
    }

    internal async Task<Guid> CreateChannelAsync(CellNode node, Guid organizationId, string username, string name)
    {
        using var response = await PostAsync(node, $"/api/v1/orgs/{organizationId}/channels", username, new { name });
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(CancellationToken))!["id"]!.GetValue<Guid>();
    }

    internal async Task<JsonNode> SendMessageAsync(CellNode node, Guid organizationId, Guid channelId, string username, string text)
    {
        using var response = await PostAsync(
            node, $"/api/v1/orgs/{organizationId}/channels/{channelId}/messages", username, new { clientMessageId = Guid.NewGuid(), text });
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(CancellationToken))!;
    }

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

    /// <summary>Adds the account as an active member with a role that may read (and create channels).</summary>
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

    private async Task InOrganizationAsync(Guid organizationId, Func<IServiceProvider, Task> work)
    {
        await using var scope = NodeA.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<OrganizationContext>().Enter(organizationId);
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await unitOfWork.BeginAsync(CancellationToken);
        await work(scope.ServiceProvider);
        await unitOfWork.CommitAsync(CancellationToken);
    }
}
