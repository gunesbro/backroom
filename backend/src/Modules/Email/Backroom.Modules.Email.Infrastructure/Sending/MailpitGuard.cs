using System.Net;
using System.Text.RegularExpressions;

namespace Backroom.Modules.Email.Infrastructure.Sending;

/// <summary>
/// How "the SMTP target is Mailpit" is decided (docs/adr/0008, items 1 and 4). Two independent checks, both
/// hard-coded, so neither a setting nor a mistake can satisfy them by accident:
/// <list type="number">
/// <item>At startup, the configured host must be a loopback address, <c>localhost</c> or the single-label service
/// name <c>mailpit</c> (Compose or Kubernetes, same network). Any other name or address stops the service, even
/// one that contains "mailpit", so a real server cannot be put there by a configuration change.</item>
/// <item>On every connection, before anything is sent, the server's greeting must be Mailpit's own
/// (<c>220 &lt;host&gt; Mailpit ESMTP Service ready</c>). This catches another SMTP server behind an allowed name:
/// a mail relay on the local machine, or a <c>mailpit</c> name that DNS resolves somewhere else. Otherwise the
/// connection is closed and the message fails; it is never sent elsewhere. MailKit sends <c>EHLO</c> (our host name
/// only) before the check; nothing carrying an address or content is sent until it passed.</item>
/// </list>
/// The host allowlist is the boundary; the greeting check is defence in depth, because any server can send that
/// greeting. Never loosen the allowlist on the grounds that the greeting is checked.
/// </summary>
internal static partial class MailpitGuard
{
    /// <summary>The longest greeting read; Mailpit's is about 60 characters.</summary>
    public const int MaxGreetingLength = 512;

    public const string ServiceName = "mailpit";

    public static bool IsAllowedHost(string? host)
    {
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, ServiceName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var literal = host.Length > 2 && host[0] == '[' && host[^1] == ']' ? host[1..^1] : host;
        return IPAddress.TryParse(literal, out var address) && IPAddress.IsLoopback(address);
    }

    public static bool IsMailpitGreeting(string? greeting) =>
        greeting is { Length: > 0 and <= MaxGreetingLength } && GreetingPattern().IsMatch(greeting);

    // Mailpit's greeting (its SMTP server's fixed application name); the host part is the container's host name.
    [GeneratedRegex(@"\A220 [!-~]{1,253} Mailpit ESMTP Service ready\r?\n?\z", RegexOptions.CultureInvariant)]
    private static partial Regex GreetingPattern();
}
