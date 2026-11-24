using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.Options;
using Backroom.Modules.Files.Application.Scanning;
using Backroom.Modules.Files.Infrastructure.Scanning;

namespace Backroom.Modules.Files.IntegrationTests;

/// <summary>The INSTREAM protocol client against a fake clamd on loopback (no container needed).</summary>
public sealed class ClamAvScannerTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("stream: OK", true)]
    [InlineData("stream: Eicar-Test-Signature FOUND", false)]
    public void Parse_WhenTheAnswerIsClear_ReturnsTheVerdict(string answer, bool clean) =>
        ClamAvScanner.Parse(answer).Should().Be(clean ? ScanVerdict.Clean : ScanVerdict.Infected);

    [Theory]
    [InlineData("")]
    [InlineData("INSTREAM size limit exceeded. ERROR")]
    [InlineData("stream: Can't allocate memory ERROR")]
    [InlineData("stream: OK but not really")]
    public void Parse_WhenTheAnswerIsAnythingElse_IsUnavailable(string answer)
    {
        var act = () => ClamAvScanner.Parse(answer);

        act.Should().Throw<ScannerUnavailableException>();
    }

    [Fact]
    public async Task Scan_WhenTheDaemonAnswersOk_SendsTheCommandAndTheContentInBoundedChunksAsync()
    {
        var content = System.Security.Cryptography.RandomNumberGenerator.GetBytes(10_000);
        await using var daemon = new FakeClamd("stream: OK\0");

        var verdict = await new ClamAvScanner(Options(daemon.Port, chunkSize: 4096)).ScanAsync(new MemoryStream(content), content.Length, CancellationToken);

        verdict.Should().Be(ScanVerdict.Clean);
        var received = await daemon.ReceivedAsync;
        received.Command.Should().Be("zINSTREAM\0");
        received.Chunks.Select(chunk => chunk.Length).Should().Equal(4096, 4096, 1808);
        received.Chunks.SelectMany(chunk => chunk).Should().Equal(content);
        received.Terminated.Should().BeTrue("the stream ends with a zero length chunk");
    }

    [Fact]
    public async Task Scan_WhenTheDaemonFindsSomething_ReturnsInfectedAsync()
    {
        await using var daemon = new FakeClamd("stream: Eicar-Test-Signature FOUND\0");

        var verdict = await new ClamAvScanner(Options(daemon.Port)).ScanAsync(new MemoryStream([1, 2, 3]), 3, CancellationToken);

        verdict.Should().Be(ScanVerdict.Infected);
    }

    [Fact]
    public async Task Scan_WhenTheDaemonAnswersAnError_IsUnavailableAsync()
    {
        await using var daemon = new FakeClamd("INSTREAM size limit exceeded. ERROR\0");

        var act = async () => await new ClamAvScanner(Options(daemon.Port)).ScanAsync(new MemoryStream([1, 2, 3]), 3, CancellationToken);

        await act.Should().ThrowAsync<ScannerUnavailableException>();
    }

    [Fact]
    public async Task Scan_WhenTheDaemonHangsUp_IsUnavailableAsync()
    {
        await using var daemon = new FakeClamd(null);

        var act = async () => await new ClamAvScanner(Options(daemon.Port)).ScanAsync(new MemoryStream([1, 2, 3]), 3, CancellationToken);

        await act.Should().ThrowAsync<ScannerUnavailableException>();
    }

    [Fact]
    public async Task Scan_WhenNothingListens_IsUnavailableAsync()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var act = async () => await new ClamAvScanner(Options(port)).ScanAsync(new MemoryStream([1]), 1, CancellationToken);

        await act.Should().ThrowAsync<ScannerUnavailableException>();
    }

    [Fact]
    public async Task Scan_WhenTheDaemonNeverAnswers_IsUnavailableAfterTheTimeoutAsync()
    {
        await using var daemon = new FakeClamd(answer: "stream: OK\0", delayAnswer: true);

        var act = async () => await new ClamAvScanner(Options(daemon.Port, scanTimeout: TimeSpan.FromSeconds(1))).ScanAsync(new MemoryStream([1, 2, 3]), 3, CancellationToken);

        await act.Should().ThrowAsync<ScannerUnavailableException>();
    }

    [Fact]
    public async Task Scan_WhenTheContentIsLongerThanItsRecordedSize_IsUnavailableAsync()
    {
        await using var daemon = new FakeClamd("stream: OK\0");

        var act = async () => await new ClamAvScanner(Options(daemon.Port)).ScanAsync(new MemoryStream(new byte[100]), 99, CancellationToken);

        await act.Should().ThrowAsync<ScannerUnavailableException>();
    }

    [Fact]
    public async Task Scan_WhenTheCallerCancels_ThrowsCancellationAsync()
    {
        await using var daemon = new FakeClamd(answer: "stream: OK\0", delayAnswer: true);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var act = async () => await new ClamAvScanner(Options(daemon.Port, scanTimeout: TimeSpan.FromSeconds(30))).ScanAsync(new MemoryStream([1]), 1, cancel.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void Validate_WhenSettingsAreMissingOrOutOfRange_Fails()
    {
        var validator = new ClamAvOptionsValidator();

        validator.Validate(null, new ClamAvOptions()).Failed.Should().BeTrue("the host is required");
        validator.Validate(null, new ClamAvOptions { Host = "clamav", ScanTimeout = TimeSpan.FromMinutes(2) }).Failed.Should().BeTrue();
        validator.Validate(null, new ClamAvOptions { Host = "clamav", Port = 0 }).Failed.Should().BeTrue();
        validator.Validate(null, new ClamAvOptions { Host = "clamav" }).Succeeded.Should().BeTrue();
    }

    private static IOptions<ClamAvOptions> Options(int port, int chunkSize = 64 * 1024, TimeSpan? scanTimeout = null) =>
        Microsoft.Extensions.Options.Options.Create(new ClamAvOptions
        {
            Host = "127.0.0.1",
            Port = port,
            ChunkSize = chunkSize,
            ScanTimeout = scanTimeout ?? TimeSpan.FromSeconds(10),
        });

    private sealed record Received(string Command, List<byte[]> Chunks, bool Terminated);

    /// <summary>A one-connection stand-in for clamd: records what the client sends, then answers (or hangs up, or stays silent).</summary>
    private sealed class FakeClamd : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();

        public FakeClamd(string? answer, bool delayAnswer = false)
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            ReceivedAsync = Task.Run(() => ServeAsync(answer, delayAnswer));
        }

        public int Port { get; }

        public Task<Received> ReceivedAsync { get; }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            try
            {
                await ReceivedAsync;
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
            {
                // The test ended the connection.
            }

            _stop.Dispose();
        }

        private async Task<Received> ServeAsync(string? answer, bool delayAnswer)
        {
            using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
            var stream = client.GetStream();
            var command = Encoding.ASCII.GetString(await ReadExactlyAsync(stream, 10));
            var chunks = new List<byte[]>();
            var terminated = false;
            while (true)
            {
                var header = await ReadExactlyAsync(stream, 4, allowEnd: true);
                if (header.Length < 4)
                {
                    break;
                }

                var length = BinaryPrimitives.ReadInt32BigEndian(header);
                if (length == 0)
                {
                    terminated = true;
                    break;
                }

                chunks.Add(await ReadExactlyAsync(stream, length));
            }

            if (delayAnswer)
            {
                await Task.Delay(Timeout.Infinite, _stop.Token);
            }

            if (answer is not null)
            {
                await stream.WriteAsync(Encoding.ASCII.GetBytes(answer), _stop.Token);
            }

            return new Received(command, chunks, terminated);
        }

        private async Task<byte[]> ReadExactlyAsync(NetworkStream stream, int count, bool allowEnd = false)
        {
            var buffer = new byte[count];
            var total = 0;
            while (total < count)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(total), _stop.Token);
                if (read == 0)
                {
                    return allowEnd ? buffer[..total] : throw new EndOfStreamException();
                }

                total += read;
            }

            return buffer;
        }
    }
}
