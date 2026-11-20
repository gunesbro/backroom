using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Backroom.Modules.Email.IntegrationTests;

/// <summary>
/// A minimal SMTP server on a loopback port that accepts everything and records the commands it receives: a
/// stand-in for a mail server that is not Mailpit (a local relay, or a "real" provider).
/// </summary>
internal sealed class FakeSmtpServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stopping = new();
    private readonly string _greeting;
    private readonly int _splitGreetingAt;
    private readonly FakeSmtpFault _fault;
    private readonly Task _accepting;
    private int _connections;

    /// <summary>
    /// Listens with <paramref name="greeting"/>; with <paramref name="splitGreetingAt"/> above 0 it sends the greeting in two
    /// writes, 200 ms apart; <paramref name="fault"/> breaks the conversation on purpose.
    /// </summary>
    public FakeSmtpServer(string greeting = "220 relay.local ESMTP Postfix", int splitGreetingAt = 0, FakeSmtpFault fault = FakeSmtpFault.None)
    {
        _greeting = greeting;
        _splitGreetingAt = splitGreetingAt;
        _fault = fault;
        _listener.Start();
        _accepting = AcceptAsync();
    }

    public static string Host => "127.0.0.1";

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public int Connections => Volatile.Read(ref _connections);

    /// <summary>Command verbs received (EHLO, MAIL, RCPT, DATA, QUIT, ...), never their arguments.</summary>
    public ConcurrentQueue<string> Commands { get; } = new();

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        _listener.Stop();
        try
        {
            await _accepting;
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SocketException)
        {
        }

        _stopping.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            var client = await _listener.AcceptTcpClientAsync(_stopping.Token);
            Interlocked.Increment(ref _connections);
            _ = ServeAsync(client);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII);
            await using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
            if (_splitGreetingAt > 0)
            {
                await writer.WriteAsync(_greeting[.._splitGreetingAt]);
                await Task.Delay(200, _stopping.Token);
                await writer.WriteLineAsync(_greeting[_splitGreetingAt..]);
            }
            else
            {
                await writer.WriteLineAsync(_greeting);
            }

            while (await reader.ReadLineAsync(_stopping.Token) is { } line)
            {
                var verb = line.Split(' ', 2)[0].ToUpperInvariant();
                Commands.Enqueue(verb);
                switch (verb)
                {
                    case "EHLO" when _fault == FakeSmtpFault.StallOnEhlo:
                        await Task.Delay(Timeout.Infinite, _stopping.Token);
                        return;
                    case "EHLO":
                        await writer.WriteLineAsync("250-relay.local");
                        await writer.WriteLineAsync("250 8BITMIME");
                        break;
                    case "DATA":
                        await writer.WriteLineAsync("354 go ahead");
                        while (await reader.ReadLineAsync(_stopping.Token) is { } data && data != ".")
                        {
                        }

                        switch (_fault)
                        {
                            case FakeSmtpFault.CloseBeforeDataReply:
                                return;
                            case FakeSmtpFault.CloseDuringDataReply:
                                await writer.WriteAsync("250 2.0");
                                return;
                            case FakeSmtpFault.CloseAfterAccepting:
                                await writer.WriteLineAsync("250 2.0.0 queued");
                                return;
                            default:
                                await writer.WriteLineAsync("250 2.0.0 queued");
                                break;
                        }

                        break;
                    case "QUIT":
                        await writer.WriteLineAsync("221 bye");
                        return;
                    default:
                        await writer.WriteLineAsync("250 OK");
                        break;
                }
            }
        }
    }
}
