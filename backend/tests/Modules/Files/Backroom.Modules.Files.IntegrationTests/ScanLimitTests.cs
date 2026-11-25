using AwesomeAssertions;
using Microsoft.Extensions.Options;
using Backroom.Modules.Files.Application.Scanning;
using Backroom.Modules.Files.Infrastructure.Scanning;
using Backroom.Testing.ClamAv;

namespace Backroom.Modules.Files.IntegrationTests;

/// <summary>Content the daemon cannot scan within its limits is reported as found, never as clean (AlertExceedsMax).</summary>
public sealed class ScanLimitTests : IAsyncLifetime
{
    private readonly ClamAvServer _daemon = ClamAvServer.WithLimits("1M", "1M");

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _daemon.InitializeAsync();

    public ValueTask DisposeAsync() => _daemon.DisposeAsync();

    [Fact]
    public async Task Scan_WhenTheContentIsOverTheDaemonsLimit_IsNotCleanAsync()
    {
        var scanner = new ClamAvScanner(Options.Create(new ClamAvOptions { Host = _daemon.Host, Port = _daemon.MappedPort }));
        var content = new byte[3 * 1024 * 1024];

        var verdict = await scanner.ScanAsync(new MemoryStream(content), content.Length, CancellationToken);

        verdict.Should().Be(ScanVerdict.Infected);
    }

    [Fact]
    public async Task Scan_WhenTheContentIsUnderTheLimit_IsCleanAsync()
    {
        var scanner = new ClamAvScanner(Options.Create(new ClamAvOptions { Host = _daemon.Host, Port = _daemon.MappedPort }));
        var content = new byte[1024];

        var verdict = await scanner.ScanAsync(new MemoryStream(content), content.Length, CancellationToken);

        verdict.Should().Be(ScanVerdict.Clean);
    }
}
