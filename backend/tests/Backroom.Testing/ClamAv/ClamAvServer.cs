using System.Net.Sockets;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace Backroom.Testing.ClamAv;

/// <summary>
/// ClamAV (clamd over TCP) in a container for integration tests, the development malware scanner. It runs offline: it
/// starts without downloading signatures and knows exactly one, the EICAR test file (<see cref="Eicar"/>), so tests are
/// fast and need no network. Share one instance per test assembly.
/// </summary>
#pragma warning disable CS0618 // the FilePath overload needs a newer package than the repo pins
public sealed class ClamAvServer : IAsyncLifetime
{
    /// <summary>The same version as the AppHost's ClamAV (checked by <c>ClamAvVersionTests</c>).</summary>
    public const string Version = "1.5.4";

    /// <summary>The standard anti-malware test string; every scanner reports it as infected and it is harmless.</summary>
    public const string Eicar = @"X5O!P%@AP[4\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*";

    private const string Image = "clamav/clamav:" + Version;
    private const int Port = 3310;

    // MD5 of the EICAR string, its length and a name: a hash signature that makes the daemon start and detect it.
    private const string EicarSignature = "44d88612fea8a8f36de82e1278abb02f:68:Backroom.Test.EICAR\n";

    private readonly IContainer _container;

    public ClamAvServer()
    {
        _container = new ContainerBuilder(Image)
            .WithEnvironment("CLAMAV_NO_FRESHCLAMD", "true")
            .WithEnvironment("CLAMAV_NO_FRESHCLAM", "true")
            .WithEnvironment("CLAMD_CONF_StreamMaxLength", "100M")
            .WithEnvironment("CLAMD_CONF_MaxFileSize", "100M")
            .WithEnvironment("CLAMD_CONF_MaxScanSize", "200M")
            .WithResourceMapping(Encoding.ASCII.GetBytes(EicarSignature), "/var/lib/clamav/eicar.hdb")
            .WithPortBinding(Port, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("socket found, clamd started"))
            .Build();
    }

    public string Host => _container.Hostname;

    public int MappedPort => _container.GetMappedPublicPort(Port);

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        // The log line comes just before the socket accepts; wait for a PONG.
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (await PingAsync())
            {
                return;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException("ClamAV did not answer PING.");
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();

    private async Task<bool> PingAsync()
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(Host, MappedPort);
            var stream = client.GetStream();
            await stream.WriteAsync("zPING\0"u8.ToArray());
            var buffer = new byte[16];
            var read = await stream.ReadAsync(buffer);
            return Encoding.ASCII.GetString(buffer, 0, read).StartsWith("PONG", StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or SocketException)
        {
            return false;
        }
    }
}
