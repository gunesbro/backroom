using System.Security.Cryptography;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace Backroom.Testing.SeaweedFs;

/// <summary>
/// SeaweedFS with its S3 API in a container for integration tests, the development object storage (MinIO is not
/// used). It accepts only requests signed with <see cref="AccessKeyId"/> and <see cref="SecretAccessKey"/>, generated
/// per run, as in development. Share one instance per test assembly; tests use their own buckets or keys.
/// </summary>
public sealed class SeaweedFsServer : IAsyncLifetime
{
    /// <summary>The same version as the AppHost's SeaweedFS (checked by <c>SeaweedFsVersionTests</c>).</summary>
    public const string Version = "4.50";

    private const string Image = "chrislusf/seaweedfs:" + Version;
    private const int S3Port = 8333;

    private readonly IContainer _container;

    public SeaweedFsServer()
    {
        AccessKeyId = "test-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        SecretAccessKey = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        _container = new ContainerBuilder(Image)
            .WithEnvironment("AWS_ACCESS_KEY_ID", AccessKeyId)
            .WithEnvironment("AWS_SECRET_ACCESS_KEY", SecretAccessKey)
            .WithCommand("server", "-dir=/data", "-s3", $"-s3.port={S3Port}")
            .WithPortBinding(S3Port, assignRandomHostPort: true)
            // An unsigned request is refused once the S3 API is up.
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request
                .ForPort(S3Port)
                .ForPath("/")
                .ForStatusCodeMatching(status => (int)status is 200 or 403)))
            .Build();
    }

    public string AccessKeyId { get; }

    public string SecretAccessKey { get; }

    /// <summary>The S3 endpoint on the host.</summary>
    public Uri Url => new($"http://{_container.Hostname}:{_container.GetMappedPublicPort(S3Port)}/");

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
