using System.Net;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Backroom.EndToEndTests;

/// <summary>
/// The real AppHost in an isolated run (fresh containers without volumes, random ports, its own key directories), so it
/// never touches a developer's running environment. Starts only with <c>BACKROOM_E2E=true</c>; otherwise every test skips.
/// </summary>
public sealed class EndToEndEnvironment : IAsyncLifetime
{
    /// <summary>The variable that turns the tests on: they need Docker and take minutes, so the solution's test run skips them.</summary>
    public const string EnabledVariable = "BACKROOM_E2E";

    /// <summary>The first test user, who creates the organizations; the second is invited in the nightly set.</summary>
    public const string Alice = "alice@example.test";

    public const string Bob = "bob@example.test";

    /// <summary>The AppHost's cells (DevelopmentCells.All).</summary>
    public static readonly IReadOnlyList<string> CellIds = ["a", "b"];

    // Pulling images on a fresh machine takes most of it.
    private static readonly TimeSpan s_startDeadline = TimeSpan.FromMinutes(10);

    private static readonly string[] s_hosts = ["control-plane", "cell-a-app", "cell-a-worker", "cell-b-app", "cell-b-worker"];

    private static readonly (string Resource, string Name)[] s_developmentPorts =
    [
        ("control-plane", "http"), ("control-plane", "https"), ("keycloak", "http"), ("mailpit", "http"), ("mailpit", "smtp"),
        ("cell-a-app", "http"), ("cell-a-worker", "http"), ("cell-b-app", "http"), ("cell-b-worker", "http"),
    ];

    private static readonly HashSet<int> s_developmentPortNumbers = [5100, 7100, 8443, 8025, 1025, 5200, 5201, 5202, 5203];

    private readonly SemaphoreSlim _organizationsLock = new(1, 1);
    private DistributedApplication? _app;
    private DirectoryInfo? _keyDirectoryRoot;
    private HttpClient? _http;
    private string? _testUserPassword;
    private SignedInPerson? _owner;
    private IReadOnlyList<TestOrganization>? _organizations;

    public static bool Enabled =>
        string.Equals(Environment.GetEnvironmentVariable(EnabledVariable), "true", StringComparison.OrdinalIgnoreCase);

    internal HttpClient Http => _http ?? throw NotStarted();

    internal Uri ControlPlaneUrl { get; private set; } = null!;

    /// <summary>The realm's OpenID Connect endpoints, ending with a slash.</summary>
    internal Uri OpenIdConnectUrl { get; private set; } = null!;

    internal Uri MailpitUrl { get; private set; } = null!;

    /// <summary>The test users' password: the run's generated <c>dev-user-password</c> parameter. Never printed.</summary>
    internal string TestUserPassword => _testUserPassword ?? throw NotStarted();

    internal Uri CellUrl(string cellId) => (_app ?? throw NotStarted()).GetEndpoint($"cell-{cellId}-app", "http");

    internal Uri ControlPlane(string path) => new(ControlPlaneUrl, path);

    public async ValueTask InitializeAsync()
    {
        if (!Enabled)
        {
            return;
        }

        using var timeout = new CancellationTokenSource(s_startDeadline);
        _keyDirectoryRoot = Directory.CreateTempSubdirectory("backroom-e2e-");
        string[] args =
        [
            "--Backroom:Isolated=true",
            $"--Backroom:KeyDirectoryRoot={_keyDirectoryRoot.FullName}",
            // Aspire testing randomizes the proxied ports by default; set here so the isolation never depends on that default.
            "--DcpPublisher:RandomizePorts=true",
        ];
        var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.Backroom_AppHost>(
            args, (options, _) => options.DisableDashboard = true, timeout.Token);
        // The hosts' and containers' own logs stay out of the test output unless they warn.
        builder.Services.AddLogging(logging => logging
            .AddFilter("Aspire.Hosting", LogLevel.Warning)
            .AddFilter("Backroom.AppHost.Resources", LogLevel.Warning));
        _app = await builder.BuildAsync(timeout.Token);
        await _app.StartAsync(timeout.Token);
        foreach (var host in s_hosts)
        {
            await _app.ResourceNotifications.WaitForResourceHealthyAsync(host, timeout.Token);
        }

        _http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false });
        ControlPlaneUrl = _app.GetEndpoint("control-plane", "http");
        OpenIdConnectUrl = new Uri(_app.GetEndpoint("keycloak", "http"), "/realms/backroom/protocol/openid-connect/");
        MailpitUrl = _app.GetEndpoint("mailpit", "http");
        RequireNoDevelopmentPorts();
        var password = _app.Services.GetRequiredService<DistributedApplicationModel>().Resources
            .OfType<ParameterResource>()
            .Single(parameter => parameter.Name == "dev-user-password");
        _testUserPassword = await password.GetValueAsync(timeout.Token);
    }

    /// <summary>
    /// Waits until both cells have registered (their workers do on first start) and the control plane lists them.
    /// </summary>
    internal async Task<IReadOnlyList<string>> SharedCellIdsAsync(CancellationToken cancellationToken) =>
        await Eventually.UntilAsync(
            async token =>
            {
                var response = await Api.GetAsync(Http, ControlPlane("/api/v1/cells/shared"), token: null, token);
                return response.Status == HttpStatusCode.OK
                    ? response.Json["cells"]!.AsArray().Select(cell => cell!["id"]!.GetValue<string>()).Order(StringComparer.Ordinal).ToList()
                    : [];
            },
            ids => ids.Count == CellIds.Count,
            "Both cells registering",
            cancellationToken,
            TimeSpan.FromMinutes(2));

    /// <summary>
    /// One organization on each cell, owned by <see cref="Alice"/> and created once per run (flow 2). A fresh run has none,
    /// and the control plane places a new organization on the cell holding the fewest, so two creations in a row land on
    /// both cells. Created once because an account may create only 5 organizations a day.
    /// </summary>
    internal async Task<IReadOnlyList<TestOrganization>> OrganizationsAsync(CancellationToken cancellationToken)
    {
        await _organizationsLock.WaitAsync(cancellationToken);
        try
        {
            if (_organizations is not null)
            {
                return _organizations;
            }

            var cellIds = await SharedCellIdsAsync(cancellationToken);
            await HeartbeatsFromAllCellsAsync(cancellationToken);
            _owner = await SignedInPerson.SignInAsync(this, Alice, cellIds, cancellationToken);
            var organizations = new List<TestOrganization>();
            for (var i = 0; i < CellIds.Count; i++)
            {
                organizations.Add(await CreateOrganizationAsync(_owner, $"End-to-end {i + 1}", cancellationToken));
            }

            _organizations = organizations;
            return organizations;
        }
        finally
        {
            _organizationsLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _owner?.Dispose();
        _organizationsLock.Dispose();
        _http?.Dispose();
        try
        {
            if (_app is not null)
            {
                await _app.DisposeAsync();
            }
        }
        finally
        {
            _keyDirectoryRoot?.Delete(recursive: true);
        }
    }

    // The control plane places organizations only on cells with a recent link heartbeat (flow 2, step 2), and a cell's
    // first heartbeat comes up to 30 seconds after it connects. No API tells a person that, so the test asks the control
    // plane's database.
    private async Task HeartbeatsFromAllCellsAsync(CancellationToken cancellationToken)
    {
        var connectionString = await (_app ?? throw NotStarted()).GetConnectionStringAsync("control-plane-db", cancellationToken);
        await using var dataSource = NpgsqlDataSource.Create(connectionString!);
        await Eventually.UntilAsync(
            async token =>
            {
                await using var command = dataSource.CreateCommand(
                    "SELECT count(*) FROM cell_management.cells WHERE status = 'registered' AND last_heartbeat_at IS NOT NULL");
                return (long)(await command.ExecuteScalarAsync(token))!;
            },
            count => count == CellIds.Count,
            "A link heartbeat from every cell",
            cancellationToken,
            TimeSpan.FromMinutes(2));
    }

    private async Task<TestOrganization> CreateOrganizationAsync(SignedInPerson owner, string name, CancellationToken cancellationToken)
    {
        var token = await owner.TokenForAsync("control-plane", cancellationToken);
        var created = await Api.PostAsync(Http, ControlPlane("/api/v1/organizations"), token.Value, new { name, region = "eu-dev" }, cancellationToken);
        if (created.Status != HttpStatusCode.Accepted)
        {
            throw new InvalidOperationException($"Creating an organization answered {created}.");
        }

        var id = created.Json["id"]!.GetValue<Guid>();
        var state = await Eventually.UntilAsync(
            async probe => (await Api.GetAsync(Http, ControlPlane($"/api/v1/organizations/{id}"), token.Value, probe)).Json["status"]!.GetValue<string>(),
            status => status != "provisioning",
            "The organization leaving provisioning",
            cancellationToken);
        if (state != "active")
        {
            throw new InvalidOperationException($"The organization ended as {state}.");
        }

        return new TestOrganization(id, created.Json["cellId"]!.GetValue<string>(), new Uri(created.Json["cellUrl"]!.GetValue<string>()));
    }

    // The isolation guard: a run that got a development port (AppHost launch profiles and DevelopmentServices) would
    // collide with, or talk to, a developer's running AppHost.
    private void RequireNoDevelopmentPorts()
    {
        var app = _app ?? throw NotStarted();
        var used = s_developmentPorts
            .Select(endpoint => (endpoint.Resource, endpoint.Name, app.GetEndpoint(endpoint.Resource, endpoint.Name).Port))
            .Where(endpoint => s_developmentPortNumbers.Contains(endpoint.Port))
            .ToList();
        if (used.Count > 0)
        {
            throw new InvalidOperationException(
                "The isolated run got development ports: " + string.Join(", ", used.Select(endpoint => $"{endpoint.Resource}/{endpoint.Name}:{endpoint.Port}")));
        }
    }

    private static InvalidOperationException NotStarted() =>
        new($"The end-to-end environment has not started; set {EnabledVariable}=true.");
}
