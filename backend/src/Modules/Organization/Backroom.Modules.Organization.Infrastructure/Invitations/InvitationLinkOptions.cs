namespace Backroom.Modules.Organization.Infrastructure.Invitations;

/// <summary>
/// Where invitation links point (section <c>Organization:InvitationLinks</c>), checked at startup in the worker role by
/// <see cref="InvitationLinkOptionsValidator"/>: the web app's origin, the only host an invitation email links to.
/// </summary>
public sealed class InvitationLinkOptions
{
    public const string SectionName = "Organization:InvitationLinks";

    /// <summary>
    /// The web app's origin, such as <c>https://app.example.com</c>: <c>https</c>, no path, query, fragment or user
    /// information. Only Development may use <c>http</c>, and only on a loopback host (the local dev server).
    /// </summary>
    public Uri? WebOrigin { get; set; }
}
