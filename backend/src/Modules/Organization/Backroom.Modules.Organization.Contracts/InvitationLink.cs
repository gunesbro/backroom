namespace Backroom.Modules.Organization.Contracts;

/// <summary>
/// What the invitation email needs (docs/contracts/phase-1.md, flow 3, step 3): the link, which carries the code in its
/// fragment, the invited address and the organization's name. It is a secret for as long as the invitation is
/// pending: never log, store or put it in an event. <see cref="ToString"/> shows none of it.
/// </summary>
public sealed class InvitationLink
{
    public InvitationLink(Uri url, string email, string organizationName)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentException.ThrowIfNullOrEmpty(email);
        ArgumentException.ThrowIfNullOrEmpty(organizationName);
        Url = url;
        Email = email;
        OrganizationName = organizationName;
    }

    /// <summary><c>https://&lt;web origin&gt;/invite#&lt;code&gt;</c>.</summary>
    public Uri Url { get; }

    /// <summary>The invited address, normalized when the invitation was created.</summary>
    public string Email { get; }

    /// <summary>The organization's name as its owner wrote it: untrusted text.</summary>
    public string OrganizationName { get; }

    /// <summary>The type name only, so a log line or exception message that formats it reveals nothing.</summary>
    public override string ToString() => nameof(InvitationLink);
}
