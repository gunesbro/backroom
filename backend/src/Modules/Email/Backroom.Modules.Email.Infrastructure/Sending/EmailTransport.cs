namespace Backroom.Modules.Email.Infrastructure.Sending;

/// <summary>
/// The one server the cell sends to, decided once at startup. This is the only place that chooses it: an
/// organization's own SMTP settings (later phases) must go through it too, so outside real sending they are ignored.
/// </summary>
internal sealed record EmailTransport(EmailTransportMode Mode, string Host, int Port, SmtpSecurity? Security, string? UserName, string? Password)
{
    /// <summary>
    /// Real sending only with both switches: the Production environment and <see cref="EmailOptions.AllowRealSending"/>.
    /// In every other case Mailpit, whatever else is configured.
    /// </summary>
    public static bool IsRealSending(EmailOptions options, bool isProduction)
    {
        ArgumentNullException.ThrowIfNull(options);
        return isProduction && options.AllowRealSending;
    }

    public static EmailTransport Resolve(EmailOptions options, bool isProduction)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (IsRealSending(options, isProduction))
        {
            var smtp = options.Smtp;
            return new EmailTransport(EmailTransportMode.Real, smtp.Host, smtp.Port, smtp.Security, NullIfEmpty(smtp.UserName), NullIfEmpty(smtp.Password));
        }

        // Only the Mailpit section is read here; the real server's settings never reach the sender.
        return new EmailTransport(EmailTransportMode.Mailpit, options.Mailpit.Host, options.Mailpit.Port, null, null, null);
    }

    // The password stays out of logs and exception messages if the record is ever printed.
    public override string ToString() => $"{Mode} {Host}:{Port}";

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
