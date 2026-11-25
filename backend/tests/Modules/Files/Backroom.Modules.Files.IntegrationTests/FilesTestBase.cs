using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Backroom.Modules.Files.Application.Scanning;
using Backroom.Modules.Files.Application.Storage;
using Backroom.Modules.Files.Infrastructure.Scanning;
using Backroom.BuildingBlocks.Application.Organizations;
using Backroom.BuildingBlocks.Application.Persistence;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.BuildingBlocks.Presentation.Authentication;
using Backroom.Modules.Files.Infrastructure;
using Backroom.Modules.Files.Presentation;
using Backroom.Modules.Messaging.Infrastructure;
using Backroom.Modules.Messaging.Presentation;
using Backroom.Modules.Organization.Contracts;
using Backroom.Modules.Organization.Infrastructure;
using Backroom.Modules.Permissions.Contracts;
using Backroom.Modules.Permissions.Infrastructure;
using Backroom.Modules.Realtime.Contracts;
using Backroom.Testing.ClamAv;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;
using Backroom.Testing.SeaweedFs;

namespace Backroom.Modules.Files.IntegrationTests;

/// <summary>
/// A cell with Files, Messaging, Organization and Permissions on a test server, with real tokens, real PostgreSQL and
/// row-level security, and real SeaweedFS. The logs are captured.
/// </summary>
public abstract class FilesTestBase(PostgresDatabase database, KeycloakRealm keycloak, SeaweedFsServer storage, ClamAvServer scanner) : IAsyncLifetime
{
    protected const string Alice = "alice@example.test";
    protected const string Bob = "bob@example.test";

    /// <summary>The bucket of these tests' cell.</summary>
    protected const string Bucket = "backroom-cell-test";

    private readonly Dictionary<string, string> _tokens = [];
    private WebApplication _cell = null!;
    private HttpClient _client = null!;
    private string _cellId = null!;

    protected static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    internal CapturingLoggerProvider Logs { get; } = new();

    internal RecordingLiveEventPublisher Live { get; } = new();

    protected IServiceProvider Services => _cell.Services;

    protected SeaweedFsServer Storage => storage;

    protected string ScannerHost => scanner.Host;

    protected int ScannerPort => scanner.MappedPort;

    /// <summary>A client outside the cell, as a browser or the desktop app talks to object storage.</summary>
    protected HttpClient StorageClient { get; } = new();

    public async ValueTask InitializeAsync()
    {
        await FilesSchema.MigrateOnceAsync(database, CancellationToken);
        _cellId = await keycloak.CreateCellScopeAsync();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        builder.Logging.AddProvider(Logs);
        builder.Configuration["Authentication:Authority"] = keycloak.Url;
        builder.Configuration["Database:ConnectionString"] = database.ApplicationConnectionString();
        builder.Configuration["Files:Storage:ServiceUrl"] = storage.Url.ToString();
        builder.Configuration["Files:Storage:Bucket"] = Bucket;
        builder.Configuration["Files:Storage:AccessKeyId"] = storage.AccessKeyId;
        builder.Configuration["Files:Storage:SecretAccessKey"] = storage.SecretAccessKey;
        builder.Configuration["Files:Storage:CreateBucket"] = "true";
        builder.AddAccessTokenAuthentication(Audiences.Cell(_cellId), [KeycloakClients.Web, KeycloakClients.Desktop]);
        builder.Services.AddDatabaseSession().BindConfiguration(DatabaseOptions.SectionName);
        builder.Services.AddPermissions();
        builder.Services.AddOrganization();
        builder.Services.AddSingleton<ILiveEventPublisher>(Live);
        builder.Services.AddSingleton<Backroom.BuildingBlocks.Application.Cells.ICellEpoch, FixedCellEpoch>();
        ConfigureServices(builder.Services);
        builder.Services.AddMessaging();
        builder.Services.AddFiles().BindConfiguration("Files:Storage");
        // Development would throw on a bad query string before the filters; production answers through them.
        builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);

        _cell = builder.Build();
        _cell.UseAuthentication();
        _cell.UseAuthorization();
        _cell.MapMessagingEndpoints();
        _cell.MapFilesEndpoints();
        await _cell.StartAsync(CancellationToken);
        _client = _cell.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        StorageClient.Dispose();
        await _cell.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    /// <summary>Lets a test class replace services (such as the clock) before the modules add theirs.</summary>
    protected virtual void ConfigureServices(IServiceCollection services)
    {
    }

    protected async Task<Guid> AccountAsync(string username) => Guid.Parse(await keycloak.UserIdAsync(username));

    protected async Task<HttpResponseMessage> GetAsync(string path, string? username) => await SendAsync(HttpMethod.Get, path, username, null);

    protected async Task<HttpResponseMessage> PostAsync(string path, string? username, object? body) => await SendAsync(HttpMethod.Post, path, username, body);

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

    protected static async Task<string?> ErrorCodeAsync(HttpResponseMessage response) =>
        (await ReadJsonAsync(response))["code"]?.GetValue<string>();

    protected async Task<Guid> CreateChannelAsync(Guid organizationId, string username, string name = "general")
    {
        using var response = await PostAsync($"/api/v1/orgs/{organizationId}/channels", username, new { name });
        response.EnsureSuccessStatusCode();
        return (await ReadJsonAsync(response))["id"]!.GetValue<Guid>();
    }

    /// <summary>Joins the public channel: uploading needs the right to post, which is a membership.</summary>
    protected async Task JoinChannelAsync(Guid organizationId, Guid channelId, string username)
    {
        using var response = await PostAsync($"/api/v1/orgs/{organizationId}/conversations/{channelId}/join", username, null);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Starts an upload; the answer is returned as it is.</summary>
    protected Task<HttpResponseMessage> StartUploadAsync(Guid organizationId, string username, object body) =>
        PostAsync($"/api/v1/orgs/{organizationId}/files/uploads", username, body);

    /// <summary>A started upload: its id, URL and the headers to send.</summary>
    protected async Task<StartedUpload> StartAsync(Guid organizationId, Guid channelId, string username, string name, long size, string contentType)
    {
        using var response = await StartUploadAsync(organizationId, username, new { conversationId = channelId, name, size, contentType });
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Created);
        var json = await ReadJsonAsync(response);
        var headers = json["headers"]!.AsObject().ToDictionary(pair => pair.Key, pair => pair.Value!.GetValue<string>(), StringComparer.Ordinal);
        return new StartedUpload(json["fileId"]!.GetValue<Guid>(), new Uri(json["uploadUrl"]!.GetValue<string>()), headers, json["expiresAt"]!.GetValue<DateTimeOffset>());
    }

    /// <summary>The client's <c>PUT</c> straight to storage, with the headers the start returned (or others).</summary>
    protected async Task<HttpResponseMessage> PutAsync(StartedUpload upload, byte[] content, IReadOnlyDictionary<string, string>? headers = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, upload.Url) { Content = new ByteArrayContent(content) };
        foreach (var (name, value) in headers ?? upload.Headers)
        {
            if (!request.Headers.TryAddWithoutValidation(name, value))
            {
                request.Content.Headers.TryAddWithoutValidation(name, value).Should().BeTrue();
            }
        }

        return await StorageClient.SendAsync(request, CancellationToken);
    }

    protected Task<HttpResponseMessage> CompleteAsync(Guid organizationId, Guid fileId, string username) =>
        PostAsync($"/api/v1/orgs/{organizationId}/files/{fileId}/complete", username, null);

    protected Task<HttpResponseMessage> DownloadAsync(Guid organizationId, Guid fileId, string username) =>
        GetAsync($"/api/v1/orgs/{organizationId}/files/{fileId}/download", username);

    /// <summary>Starts, uploads, completes and scans a file: it is ready afterwards (the real scanner, as the worker runs it).</summary>
    protected async Task<Guid> UploadAsync(Guid organizationId, Guid channelId, string username, byte[] content, string contentType = "image/png", string name = "photo.png")
    {
        var upload = await StartAsync(organizationId, channelId, username, name, content.Length, contentType);
        using (var put = await PutAsync(upload, content))
        {
            put.EnsureSuccessStatusCode();
        }

        using var completed = await CompleteAsync(organizationId, upload.FileId, username);
        completed.EnsureSuccessStatusCode();
        await RunScanJobAsync();
        return upload.FileId;
    }

    internal FileScanJob NewScanJob(IFileScanner? scannerOverride = null)
    {
        var options = Options.Create(new ClamAvOptions { Host = scanner.Host, Port = scanner.MappedPort, ScanTimeout = TimeSpan.FromSeconds(20) });
        return new FileScanJob(Services.GetRequiredService<IFileStorageProvider>(), scannerOverride ?? new ClamAvScanner(options), Live, TimeProvider.System, new LoggerFactory([Logs]).CreateLogger<FileScanJob>());
    }

    internal NpgsqlConnection NewMaintenanceConnection() => new(database.MaintenanceConnectionString);

    /// <summary>Runs the worker's scan job as the maintenance role until no file waits (ClamAV in its container, or at <paramref name="port"/>).</summary>
    internal async Task RunScanJobAsync(int? port = null, IFileScanner? scannerOverride = null)
    {
        var options = Options.Create(new ClamAvOptions { Host = scanner.Host, Port = port ?? scanner.MappedPort, ScanTimeout = TimeSpan.FromSeconds(20) });
        using var job = new FileScanJob(Services.GetRequiredService<IFileStorageProvider>(), scannerOverride ?? new ClamAvScanner(options), Live, TimeProvider.System, new LoggerFactory([Logs]).CreateLogger<FileScanJob>());
        await using var connection = new NpgsqlConnection(database.MaintenanceConnectionString);
        await connection.OpenAsync(CancellationToken);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(120);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var transaction = await connection.BeginTransactionAsync(CancellationToken);
            var handled = await job.RunBatchAsync(connection, transaction, CancellationToken);
            await transaction.CommitAsync(CancellationToken);
            if (handled == 0 && job.PendingWork == 0)
            {
                break;
            }

            await Task.Delay(50, CancellationToken);
        }
    }

    /// <summary>Writes an object straight into the cell's bucket, bypassing the signed URL (as a store that ignored its conditions would).</summary>
    protected async Task PutObjectDirectlyAsync(Guid organizationId, Guid fileId, byte[] content)
    {
        var config = new Amazon.S3.AmazonS3Config
        {
            ServiceURL = storage.Url.ToString(),
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1",
            RequestChecksumCalculation = Amazon.Runtime.RequestChecksumCalculation.WHEN_REQUIRED,
        };
        using var client = new Amazon.S3.AmazonS3Client(new Amazon.Runtime.BasicAWSCredentials(storage.AccessKeyId, storage.SecretAccessKey), config);
        using var body = new MemoryStream(content);
        await client.PutObjectAsync(
            new Amazon.S3.Model.PutObjectRequest { BucketName = Bucket, Key = $"{organizationId}/{fileId}", InputStream = body, ContentType = "image/png" },
            CancellationToken);
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
    protected async Task<object?> ScalarAsync(string sql, params object[] parameters)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        }

        return await command.ExecuteScalarAsync(CancellationToken);
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

    protected sealed record StartedUpload(Guid FileId, Uri Url, IReadOnlyDictionary<string, string> Headers, DateTimeOffset ExpiresAt);

    private sealed class FixedCellEpoch : Backroom.BuildingBlocks.Application.Cells.ICellEpoch
    {
        public ValueTask<long?> ConfirmedEpochAsync(CancellationToken cancellationToken) => ValueTask.FromResult<long?>(1);
    }

    /// <summary>Records the <c>FileRejected</c> notices; the other events are not Files' concern.</summary>
    internal sealed class RecordingLiveEventPublisher : ILiveEventPublisher
    {
        private readonly Lock _lock = new();
        private readonly List<FileRejectedPayload> _rejected = [];

        public IReadOnlyList<FileRejectedPayload> Rejected
        {
            get
            {
                lock (_lock)
                {
                    return [.. _rejected];
                }
            }
        }

        public Task PublishAsync(MessagePostedPayload payload, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task PublishAsync(MessagePostedV2Payload payload, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task PublishAsync(ChannelCreatedPayload payload, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task PublishAsync(PresenceChangedPayload payload, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task PublishAsync(FileRejectedPayload payload, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                _rejected.Add(payload);
            }

            return Task.CompletedTask;
        }

        public Task PublishAsync(NotificationRequestedPayload payload, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task PublishAsync(ConversationUpdatedPayload payload, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task PublishAsync(MembershipChangedPayload payload, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RemoveFromConversationAsync(Guid orgId, Guid conversationId, Guid accountId, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
