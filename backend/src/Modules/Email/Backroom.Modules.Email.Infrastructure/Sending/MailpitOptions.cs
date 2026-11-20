namespace Backroom.Modules.Email.Infrastructure.Sending;

/// <summary>Where Mailpit's SMTP port is. The host must pass <see cref="MailpitGuard.IsAllowedHost"/>.</summary>
public sealed class MailpitOptions
{
    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 1025;
}
