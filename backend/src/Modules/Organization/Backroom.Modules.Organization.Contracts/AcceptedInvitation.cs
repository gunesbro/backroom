namespace Backroom.Modules.Organization.Contracts;

/// <summary>
/// An invitation the control plane accepted for an account (<c>InvitationAccepted</c>, docs/contracts/phase-1.md,
/// flow 3, step 5). The profile values come from the accepting account's token; <paramref name="AcceptedAt"/> is the
/// control plane's time of acceptance, which the cell checks against the invitation's expiry.
/// </summary>
public sealed record AcceptedInvitation(
    Guid InvitationId,
    Guid AccountId,
    string DisplayName,
    string Email,
    string Locale,
    DateTimeOffset AcceptedAt);
