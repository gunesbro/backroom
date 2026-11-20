namespace Backroom.Modules.Organization.Infrastructure.Invitations;

/// <summary>
/// The cell's versioned invitation keys (docs/contracts/phase-1.md, "Invitation security"). They come from the
/// cell's secret store (user secrets or environment variables in development), never from <c>appsettings</c>
/// or the database. A missing or weak key stops the service at startup.
/// </summary>
public sealed class InvitationKeyOptions
{
    public const string SectionName = "Organization:InvitationKeys";

    /// <summary>HMAC-SHA256 keys shorter than its output would weaken the code.</summary>
    public const int MinKeyBytes = 32;

    /// <summary>A rotated-out version is deleted 7 days later, so only a few exist at a time.</summary>
    public const int MaxVersions = 8;

    /// <summary>The version new codes are issued with.</summary>
    public int CurrentVersion { get; set; }

    /// <summary>Version number → key, base64 of at least <see cref="MinKeyBytes"/> random bytes.</summary>
    public IDictionary<string, string> Keys { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
}
