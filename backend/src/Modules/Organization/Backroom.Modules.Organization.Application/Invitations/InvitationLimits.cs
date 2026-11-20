namespace Backroom.Modules.Organization.Application.Invitations;

/// <summary>The anti-spam limits and lifetime of invitations (docs/contracts/phase-1.md, flow 3, steps 1 and 2).</summary>
internal static class InvitationLimits
{
    public const int PerInviterPerDay = 50;
    public const int PerOrganizationPerDay = 200;
    public const int PerRecipientPerDay = 3;
    public const int PendingPerOrganization = 500;

    public static readonly TimeSpan Window = TimeSpan.FromDays(1);
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    /// <summary>Pending invitations free up when they are accepted, revoked or expire, at no known time.</summary>
    public static readonly TimeSpan PendingRetryAfter = TimeSpan.FromHours(1);
}
