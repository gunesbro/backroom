namespace Backroom.Modules.Organization.Application.Invitations;

/// <summary>
/// Invitations created in the organization since the start of the rate-limit window: by the inviter, by anyone and
/// to one address, each with the creation time of its oldest (when that count starts to fall again).
/// </summary>
internal sealed record RecentInvitations(
    int ByInviter,
    DateTimeOffset? OldestByInviter,
    int InOrganization,
    DateTimeOffset? OldestInOrganization,
    int ToRecipient,
    DateTimeOffset? OldestToRecipient);
