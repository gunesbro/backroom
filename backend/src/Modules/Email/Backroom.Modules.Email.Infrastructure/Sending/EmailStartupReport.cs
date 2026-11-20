using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Backroom.Modules.Email.Infrastructure.Sending;

/// <summary>
/// Says at startup where email goes, and warns when a real server is configured but not used (Production without
/// <see cref="EmailOptions.AllowRealSending"/>), so an operator who expected real sending notices before mail is
/// missed. Server host names only; no secrets.
/// </summary>
internal sealed partial class EmailStartupReport(
    EmailTransport transport,
    IOptions<EmailOptions> options,
    ILogger<EmailStartupReport> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (transport.Mode == EmailTransportMode.Real)
        {
            LogRealSending(logger, transport.Host, transport.Port);
        }
        else
        {
            LogMailpit(logger, transport.Host, transport.Port);
            if (options.Value.Smtp.IsConfigured)
            {
                LogRealServerIgnored(logger);
            }
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Email is sent for real through {Host}:{Port} (Production with Email:AllowRealSending).")]
    private static partial void LogRealSending(ILogger logger, string host, int port);

    [LoggerMessage(Level = LogLevel.Information, Message = "Email goes to Mailpit at {Host}:{Port} only.")]
    private static partial void LogMailpit(ILogger logger, string host, int port);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Email:Smtp is configured but Email:AllowRealSending is off: the real server is not used and every message goes to Mailpit.")]
    private static partial void LogRealServerIgnored(ILogger logger);
}
