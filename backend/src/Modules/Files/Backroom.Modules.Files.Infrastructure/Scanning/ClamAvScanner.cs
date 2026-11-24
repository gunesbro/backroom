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
/// deadline. Anything but a clear "OK" or "FOUND" answer, and every network failure, is
/// <see cref="ScannerUnavailableException"/>. The daemon's <c>StreamMaxLength</c> must be at least the largest file
/// (100 MB): above it clamd answers an error, which is never read as clean. Nothing the scanner or the file says is logged.
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
        try
        {
            using var client = new TcpClient { NoDelay = true };
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token))
            {
                connect.CancelAfter(settings.ConnectTimeout);
                await client.ConnectAsync(settings.Host, settings.Port, connect.Token);
            }

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
        if (answer.EndsWith(" OK", StringComparison.Ordinal))
        {
            return ScanVerdict.Clean;
        }

        if (answer.EndsWith(" FOUND", StringComparison.Ordinal))
        {
            return ScanVerdict.Infected;
        }

        throw new ScannerUnavailableException("The scanner answered with an error.");
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
            if (buffer.AsSpan(0, total).Contains((byte)0))
            {
                break;
            }
        }

        return Encoding.ASCII.GetString(buffer, 0, total).TrimEnd('\0', '\n', ' ');
    }
}
