namespace Backroom.ArchitectureTests;

/// <summary>
/// Only the Email module's infrastructure may send mail (docs/adr/0008, item 8). Mail and MIME
/// libraries and hosted mail SDKs are checked here (compiled references, types and NuGet packages);
/// raw SMTP connections over sockets are caught by the Semgrep rule in <c>.semgrep/mail.yml</c>.
/// </summary>
public static class SmtpUsageRules
{
    public const string EmailInfrastructure = "Backroom.Modules.Email.Infrastructure";

    /// <summary>Assembly or package name prefixes of libraries that send mail.</summary>
    public static IReadOnlyCollection<string> MailLibraryPrefixes { get; } =
    [
        "System.Net.Mail",
        "MailKit",
        "MimeKit",
        "SendGrid",
        "AWSSDK.SimpleEmail",
        "AWSSDK.SimpleEmailV2",
        "Azure.Communication.Email",
        "FluentEmail",
        "Postmark",
        "Mailgun",
        "MailerSend",
        "Brevo",
        "brevo_csharp",
        "Resend",
    ];

    /// <summary>Namespaces of the same libraries, for type-level checks.</summary>
    public const string MailNamespacePattern =
        @"^(System\.Net\.Mail|MailKit|MimeKit|SendGrid|Amazon\.SimpleEmail(V2)?|Azure\.Communication\.Email|FluentEmail|PostmarkDotNet|Mailgun|MailerSend|brevo_csharp|Resend)(\..*)?$";

    public static bool MayUseSmtp(string assemblyName) =>
        string.Equals(assemblyName, EmailInfrastructure, StringComparison.Ordinal);

    public static bool IsMailLibrary(string name) =>
        MailLibraryPrefixes.Any(prefix =>
            string.Equals(name, prefix, StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<string> FindViolations(string assemblyName, IEnumerable<string> referencedNames)
    {
        if (MayUseSmtp(assemblyName))
        {
            return [];
        }

        return referencedNames
            .Where(IsMailLibrary)
            .Select(name => $"{assemblyName} must not use {name}; only {EmailInfrastructure} sends mail.")
            .ToList();
    }
}
