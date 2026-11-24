using System.Buffers;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Options;
using Backroom.Modules.Files.Application.Scanning;

namespace Backroom.Modules.Files.Infrastructure.Scanning;

/// <summary>
/// <see cref="IFileScanner"/> over clamd's <c>INSTREAM</c> command (a plain TCP socket; no client library). The content
/// is streamed in chunks of <see cref="ClamAvOptions.ChunkSize"/> and never held whole; the whole scan has one
/// deadline. Only the exact answers "stream: OK" and "stream: {name} FOUND", each ended by a NUL, are verdicts; every
/// other reply (errors, truncated or unterminated text) and every network failure is
/// <see cref="ScannerUnavailableException"/>. The content must have exactly the recorded size. The daemon's limits
/// (<c>StreamMaxLength</c>, <c>MaxFileSize</c>, <c>MaxScanSize</c>) must cover the largest file (100 MB) and it must
/// run with <c>AlertExceedsMax</c> and <c>AlertEncrypted</c>, so what it cannot scan fully is reported as found and
/// never as clean. Nothing the scanner or the file says is logged.
/// </summary>
internal sealed class ClamAvScanner(IOptions<ClamAvOptions> options) : IFileScanner
{
    private const int MaxResponseBytes = 1024;
    private static readonly byte[] s_command = Encoding.ASCII.GetBytes("zINSTREAM\0");
    private static readonly byte[] s_end = [0, 0, 0, 0];

    public async Task<ScanVerdict> ScanAsync(Stream content, long length, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var settings = options.Value;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(settings.ScanTimeout);
        using var client = new TcpClient { NoDelay = true };
        try
        {
            await ConnectAsync(client, settings, deadline.Token);
        }
        catch (Exception exception) when ((exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            || exception is IOException or SocketException or ObjectDisposedException)
        {
            // Nothing was sent: the scanner is down or unreachable, which says nothing about the file.
            throw new ScannerUnavailableException("The scanner could not be reached.", exception) { IsOutage = true };
        }

        try
        {
            var stream = client.GetStream();
            await StreamContentAsync(stream, content, length, settings.ChunkSize, deadline.Token);
            return Parse(await ReadAnswerAsync(stream, deadline.Token));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ScannerUnavailableException("The scanner did not answer in time.");
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException or InvalidDataException)
        {
            throw new ScannerUnavailableException("The scanner could not be reached or answered with an error.", exception);
        }
    }

    /// <summary>"stream: OK" is clean and "stream: {name} FOUND" is infected; every other text is an error or unknown.</summary>
    internal static ScanVerdict Parse(string answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        if (answer == "stream: OK")
        {
            return ScanVerdict.Clean;
        }

        const string Prefix = "stream: ";
        const string Suffix = " FOUND";
        if (answer.Length > Prefix.Length + Suffix.Length
            && answer.StartsWith(Prefix, StringComparison.Ordinal)
            && answer.EndsWith(Suffix, StringComparison.Ordinal))
        {
            return ScanVerdict.Infected;
        }

        throw new ScannerUnavailableException("The scanner answered with an error.");
    }

    private static async Task ConnectAsync(TcpClient client, ClamAvOptions settings, CancellationToken cancellationToken)
    {
        using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connect.CancelAfter(settings.ConnectTimeout);
        await client.ConnectAsync(settings.Host, settings.Port, connect.Token);
    }

    private static async Task StreamContentAsync(NetworkStream stream, Stream content, long length, int chunkSize, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(s_command, cancellationToken);
        var buffer = ArrayPool<byte>.Shared.Rent(4 + chunkSize);
        try
        {
            long sent = 0;
            while (true)
            {
                var read = await content.ReadAsync(buffer.AsMemory(4, chunkSize), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                sent += read;
                if (sent > length)
                {
                    // More than the file's recorded size: the object is not what was checked; no answer is given for it.
                    throw new InvalidDataException("The object is larger than its recorded size.");
                }

                BinaryPrimitives.WriteInt32BigEndian(buffer, read);
                await stream.WriteAsync(buffer.AsMemory(0, 4 + read), cancellationToken);
            }

            if (sent != length)
            {
                throw new InvalidDataException("The object is shorter than its recorded size.");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        await stream.WriteAsync(s_end, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task<string> ReadAnswerAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxResponseBytes];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
            var end = buffer.AsSpan(0, total).IndexOf((byte)0);
            if (end >= 0)
            {
                return Encoding.ASCII.GetString(buffer, 0, end).TrimEnd('\n');
            }
        }

        // No terminator: the reply was cut off or is too long, so it is no answer.
        throw new ScannerUnavailableException("The scanner's reply was truncated.");
    }
}
