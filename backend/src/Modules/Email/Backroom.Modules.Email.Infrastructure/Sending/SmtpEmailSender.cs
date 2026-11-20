using System.Diagnostics.CodeAnalysis;
using System.Net;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using MimeKit.Utils;
using Backroom.Modules.Email.Application.Sending;

namespace Backroom.Modules.Email.Infrastructure.Sending;

/// <summary>
/// The SMTP adapter (MailKit): one connection per message to the server <see cref="EmailTransport"/> chose at
/// startup. To Mailpit it sends only after the server's greeting proved it is Mailpit (<see cref="MailpitGuard"/>);
/// to the real server only over TLS. A failure is returned and logged by kind and SMTP status code only, because
/// server replies and exception texts can contain addresses.
/// </summary>
internal sealed partial class SmtpEmailSender(
    EmailTransport transport,
    IOptions<EmailOptions> options,
    TimeProvider timeProvider,
    ILogger<SmtpEmailSender> logger) : IEmailSender
{
    /// <summary>For each network operation (connect, each command); a stalled server fails the message instead of blocking a worker.</summary>
    public static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(30);

    [SuppressMessage("Design", "CA1031", Justification = "A send failure is recorded in the delivery log and returned; it must not escape as an exception.")]
    public async Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        using var mime = Build(message);
        var greeting = new GreetingRecorder();
        using var client = new SmtpClient(greeting) { Timeout = (int)OperationTimeout.TotalMilliseconds };
        try
        {
            await client.ConnectAsync(transport.Host, transport.Port, SocketOptions(transport), cancellationToken);
            if (transport.Mode == EmailTransportMode.Mailpit && !MailpitGuard.IsMailpitGreeting(greeting.Greeting))
            {
                LogNotMailpit(logger, transport.Host, transport.Port);
                await client.DisconnectAsync(quit: true, cancellationToken);
                return EmailSendResult.Failed(EmailSendFailure.NotMailpit);
            }

            if (transport is { UserName: { } userName, Password: { } password })
            {
                await client.AuthenticateAsync(new NetworkCredential(userName, password), cancellationToken);
            }

            await client.SendAsync(mime, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown. Before the hand-over nothing was sent; after it the server may have the message.
            return EmailSendResult.Failed(greeting.HandoverStarted ? EmailSendFailure.Unknown : EmailSendFailure.Cancelled);
        }
        catch (SmtpCommandException exception)
        {
            // An error reply: the server answered, so it did not take the message.
            LogRejected(logger, (int)exception.StatusCode, exception.ErrorCode);
            return EmailSendResult.Failed(EmailSendFailure.Rejected);
        }
        catch (AuthenticationException)
        {
            LogAuthenticationFailed(logger, transport.Host);
            return EmailSendResult.Failed(EmailSendFailure.Rejected);
        }
        catch (Exception exception) when (greeting.HandoverStarted)
        {
            // The connection broke or timed out after DATA/BDAT, before the reply: the server may have accepted it.
            LogOutcomeUnknown(logger, transport.Mode, transport.Host, transport.Port, exception.GetType().Name);
            return EmailSendResult.Failed(EmailSendFailure.Unknown);
        }
        catch (Exception exception)
        {
            // Connection refused, timeout, TLS or protocol failure before the hand-over: the server is unavailable.
            // Only the type is logged.
            LogUnavailable(logger, transport.Mode, transport.Host, transport.Port, exception.GetType().Name);
            return EmailSendResult.Failed(EmailSendFailure.Unavailable);
        }

        // The server accepted the message: it is sent, whatever happens while the connection closes.
        try
        {
            await client.DisconnectAsync(quit: true, CancellationToken.None);
        }
        catch (Exception exception)
        {
            LogDisconnectFailed(logger, exception.GetType().Name);
        }

        // Build always sets it: the stored provider message id is our own Message-ID, which bounces refer to.
        return EmailSendResult.Sent(mime.MessageId!);
    }

    private static SecureSocketOptions SocketOptions(EmailTransport transport) => transport.Security switch
    {
        // Mailpit on the local machine or network, without TLS.
        null => SecureSocketOptions.None,
        SmtpSecurity.StartTls => SecureSocketOptions.StartTls,
        SmtpSecurity.Tls => SecureSocketOptions.SslOnConnect,
        _ => throw new InvalidOperationException("Unknown SMTP security."),
    };

    private MimeMessage Build(EmailMessage message)
    {
        var settings = options.Value;
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(message.SenderName ?? settings.FromName, settings.FromAddress));
        // The address alone: no display name is taken from anywhere.
        mime.To.Add(new MailboxAddress(string.Empty, message.To));
        mime.Subject = message.Subject;
        mime.Date = timeProvider.GetUtcNow();
        mime.MessageId = MimeUtils.GenerateMessageId(settings.FromAddress[(settings.FromAddress.LastIndexOf('@') + 1)..]);
        mime.Body = new BodyBuilder { TextBody = message.TextBody, HtmlBody = message.HtmlBody }.ToMessageBody();
        return mime;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The email server at {Host}:{Port} is not Mailpit; nothing was sent and nothing is sent elsewhere.")]
    private static partial void LogNotMailpit(ILogger logger, string host, int port);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The email server rejected a message (status {StatusCode}, {ErrorCode}).")]
    private static partial void LogRejected(ILogger logger, int statusCode, SmtpErrorCode errorCode);

    [LoggerMessage(Level = LogLevel.Error, Message = "Authentication with the email server {Host} failed.")]
    private static partial void LogAuthenticationFailed(ILogger logger, string host);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The email server ({Mode}) at {Host}:{Port} is unavailable ({ExceptionType}); the message is not rerouted.")]
    private static partial void LogUnavailable(ILogger logger, EmailTransportMode mode, string host, int port, string exceptionType);

    [LoggerMessage(Level = LogLevel.Error, Message = "The connection to the email server ({Mode}) at {Host}:{Port} broke while the message was handed over ({ExceptionType}); it may have been accepted, so it is not sent again.")]
    private static partial void LogOutcomeUnknown(ILogger logger, EmailTransportMode mode, string host, int port, string exceptionType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Closing the connection after the email server accepted a message failed ({ExceptionType}); the message is sent.")]
    private static partial void LogDisconnectFailed(ILogger logger, string exceptionType);
}
