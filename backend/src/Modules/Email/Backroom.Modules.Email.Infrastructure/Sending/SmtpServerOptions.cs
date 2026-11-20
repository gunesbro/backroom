namespace Backroom.Modules.Email.Infrastructure.Sending;

/// <summary>The real SMTP server (Production with real sending allowed only). Always over TLS.</summary>
public sealed class SmtpServerOptions
{
    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 587;

    /// <summary><see cref="SmtpSecurity.StartTls"/> (port 587) or <see cref="SmtpSecurity.Tls"/> (port 465); never plain text.</summary>
    public SmtpSecurity Security { get; set; } = SmtpSecurity.StartTls;

    public string? UserName { get; set; }

    /// <summary>A secret, from the deployment's secret store.</summary>
    public string? Password { get; set; }

    /// <summary>Whether any value of this section was set (a real server outside real sending is a startup error).</summary>
    internal bool IsConfigured =>
        !string.IsNullOrEmpty(Host) || !string.IsNullOrEmpty(UserName) || !string.IsNullOrEmpty(Password);
}
