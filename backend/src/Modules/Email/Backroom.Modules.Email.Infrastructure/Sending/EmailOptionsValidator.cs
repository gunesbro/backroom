using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Backroom.BuildingBlocks.Domain.Email;
using Backroom.Modules.Email.Application.Sending;
using Backroom.Modules.Email.Infrastructure.Deliveries;

namespace Backroom.Modules.Email.Infrastructure.Sending;

/// <summary>
/// Stops the service at startup on unsafe or incomplete email settings (docs/adr/0008). Outside Production:
/// real sending cannot be switched on, no real server may be configured (developer machines hold no provider
/// credentials) and the Mailpit host must pass <see cref="MailpitGuard.IsAllowedHost"/>. With real sending: a
/// real server over TLS, and an environment name set explicitly: .NET assumes Production when none is set, so
/// an unconfigured machine would otherwise have one of the two switches already on. Messages never name a secret's value.
/// </summary>
internal sealed class EmailOptionsValidator(IHostEnvironment environment, IConfiguration configuration) : IValidateOptions<EmailOptions>
{
    public ValidateOptionsResult Validate(string? name, EmailOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        var isProduction = environment.IsProduction();

        if (!EmailAddress.TryNormalize(options.FromAddress, out _))
        {
            failures.Add("Email:FromAddress must be one email address.");
        }

        if (string.IsNullOrWhiteSpace(options.FromName)
            || options.FromName.Length > EmailMessage.MaxSenderNameLength
            || !string.Equals(EmailMessage.StripLineBreaks(options.FromName), options.FromName, StringComparison.Ordinal))
        {
            failures.Add($"Email:FromName must have 1 to {EmailMessage.MaxSenderNameLength} characters on one line.");
        }

        if (!RecipientHasher.TryReadKey(options.RecipientHashKey, out _))
        {
            failures.Add($"Email:RecipientHashKey must be base64 of at least {EmailOptions.MinRecipientHashKeyBytes} bytes.");
        }

        if (EmailTransport.IsRealSending(options, isProduction))
        {
            ValidateRealServer(options.Smtp, failures);
            if (string.IsNullOrWhiteSpace(configuration[HostDefaults.EnvironmentKey]))
            {
                failures.Add("Real sending needs the environment set explicitly (DOTNET_ENVIRONMENT or ASPNETCORE_ENVIRONMENT=Production); .NET assumes Production when it is unset.");
            }
        }
        else
        {
            if (!isProduction && options.AllowRealSending)
            {
                failures.Add($"Email:AllowRealSending is allowed only in Production (environment: {environment.EnvironmentName}).");
            }

            if (!isProduction && options.Smtp.IsConfigured)
            {
                failures.Add("Email:Smtp must not be configured outside Production; email goes to Mailpit only.");
            }

            if (!MailpitGuard.IsAllowedHost(options.Mailpit.Host))
            {
                failures.Add("Email:Mailpit:Host must be Mailpit: a loopback address, 'localhost' or 'mailpit'. Email goes nowhere else unless real sending is allowed in Production.");
            }

            if (!IsPort(options.Mailpit.Port))
            {
                failures.Add("Email:Mailpit:Port must be between 1 and 65535.");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateRealServer(SmtpServerOptions smtp, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(smtp.Host) || Uri.CheckHostName(smtp.Host) == UriHostNameType.Unknown)
        {
            failures.Add("Email:Smtp:Host is required for real sending.");
        }

        if (!IsPort(smtp.Port))
        {
            failures.Add("Email:Smtp:Port must be between 1 and 65535.");
        }

        if (!Enum.IsDefined(smtp.Security))
        {
            failures.Add("Email:Smtp:Security must be StartTls or Tls.");
        }

        if (string.IsNullOrEmpty(smtp.UserName) != string.IsNullOrEmpty(smtp.Password))
        {
            failures.Add("Email:Smtp:UserName and Email:Smtp:Password are set together or not at all.");
        }
    }

    private static bool IsPort(int port) => port is > 0 and <= 65535;
}
