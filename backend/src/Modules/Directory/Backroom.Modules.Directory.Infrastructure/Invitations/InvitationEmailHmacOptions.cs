namespace Backroom.Modules.Directory.Infrastructure.Invitations;

/// <summary>
/// The control plane's key for invited emails (docs/contracts/phase-1.md, "Data"). It comes from the control plane's
/// secret store (user secrets or environment variables in development), never from <c>appsettings</c> or the
/// database. A missing or weak key stops the service at startup. Changing it makes stored HMACs unmatchable, so
/// pending invitations would have to be sent again.
/// </summary>
public sealed class InvitationEmailHmacOptions
{
    public const string SectionName = "Directory:Invitations";

    /// <summary>HMAC-SHA256 keys shorter than its output would weaken the HMAC.</summary>
    public const int MinKeyBytes = 32;

    /// <summary>Base64 of at least <see cref="MinKeyBytes"/> random bytes.</summary>
    public string EmailHmacKey { get; set; } = string.Empty;
}
