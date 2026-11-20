namespace Backroom.Modules.Email.Infrastructure.Sending;

/// <summary>
/// Email settings (section <c>Email</c>), checked at startup by <see cref="EmailOptionsValidator"/>. Which server
/// is used is not a setting: <see cref="EmailTransport.Resolve"/> picks the real server only in Production with
/// <see cref="AllowRealSending"/> on, and Mailpit otherwise (docs/adr/0008). Secrets (the recipient hash key and
/// the real server's password) come from the secret store or the AppHost, never from <c>appsettings</c>.
/// </summary>
public sealed class EmailOptions
{
    public const string SectionName = "Email";

    /// <summary>HMAC-SHA256 keys shorter than its output would weaken the hash.</summary>
    public const int MinRecipientHashKeyBytes = 32;

    /// <summary>The second switch for real sending; the first is the Production environment. Off by default.</summary>
    public bool AllowRealSending { get; set; }

    /// <summary>The sender address of every message (one plain address).</summary>
    public string FromAddress { get; set; } = string.Empty;

    /// <summary>The product's name, used when a template does not give a sender name.</summary>
    public string FromName { get; set; } = string.Empty;

    /// <summary>Base64 of at least <see cref="MinRecipientHashKeyBytes"/> random bytes: the key of the stored recipient hashes. A secret.</summary>
    public string RecipientHashKey { get; set; } = string.Empty;

    /// <summary>The Mailpit server every message goes to unless real sending is on.</summary>
    public MailpitOptions Mailpit { get; set; } = new();

    /// <summary>The real server, used only in Production with <see cref="AllowRealSending"/>. Must be empty everywhere else.</summary>
    public SmtpServerOptions Smtp { get; set; } = new();
}
