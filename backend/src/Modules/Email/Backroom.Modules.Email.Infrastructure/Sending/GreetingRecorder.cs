using System.Text;
using MailKit;

namespace Backroom.Modules.Email.Infrastructure.Sending;

/// <summary>
/// The protocol logger of a connection. It keeps the server's greeting (what the server sends before the client's
/// first command, at most <see cref="MailpitGuard.MaxGreetingLength"/> bytes) and notes when the client starts
/// handing over the message (a <c>DATA</c> or <c>BDAT</c> command line). Nothing else of the conversation is kept
/// or logged: commands and later replies carry addresses and content.
/// </summary>
internal sealed class GreetingRecorder : IProtocolLogger
{
    private static readonly byte[] s_data = "DATA\r\n"u8.ToArray();
    private static readonly byte[] s_bdat = "BDAT "u8.ToArray();

    private readonly byte[] _buffer = new byte[MailpitGuard.MaxGreetingLength + 1];
    private int _length;
    private bool _clientSpoke;
    private bool _atLineStart = true;

    public IAuthenticationSecretDetector? AuthenticationSecretDetector { get; set; }

    /// <summary>The greeting as ASCII, or <see langword="null"/> if it was empty or too long.</summary>
    public string? Greeting =>
        _length is 0 or > MailpitGuard.MaxGreetingLength ? null : Encoding.ASCII.GetString(_buffer, 0, _length);

    /// <summary>
    /// Whether the client has written a <c>DATA</c> or <c>BDAT</c> command: from then on the server may have the
    /// message, so a broken connection leaves the outcome unknown. Errs towards <see langword="true"/>.
    /// </summary>
    public bool HandoverStarted { get; private set; }

    public void LogConnect(Uri uri)
    {
    }

    public void LogClient(byte[] buffer, int offset, int count)
    {
        _clientSpoke = true;
        if (HandoverStarted)
        {
            return;
        }

        // Commands start at line starts; pipelining can put several in one write.
        var written = buffer.AsSpan(offset, count);
        for (var index = 0; index < written.Length; index++)
        {
            if (_atLineStart && (written[index..].StartsWith(s_data) || written[index..].StartsWith(s_bdat)))
            {
                HandoverStarted = true;
                return;
            }

            _atLineStart = written[index] == (byte)'\n';
        }
    }

    public void LogServer(byte[] buffer, int offset, int count)
    {
        if (_clientSpoke || _length >= _buffer.Length)
        {
            return;
        }

        var copied = Math.Min(count, _buffer.Length - _length);
        Buffer.BlockCopy(buffer, offset, _buffer, _length, copied);
        _length += copied;
    }

    public void Dispose()
    {
    }
}
