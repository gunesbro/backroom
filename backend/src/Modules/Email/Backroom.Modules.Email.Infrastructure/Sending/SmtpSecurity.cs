namespace Backroom.Modules.Email.Infrastructure.Sending;

/// <summary>How the real server's connection is encrypted. There is deliberately no plain-text value.</summary>
public enum SmtpSecurity
{
    /// <summary>Plain connection upgraded with STARTTLS, which must succeed (usually port 587).</summary>
    StartTls,

    /// <summary>TLS from the first byte (usually port 465).</summary>
    Tls,
}
