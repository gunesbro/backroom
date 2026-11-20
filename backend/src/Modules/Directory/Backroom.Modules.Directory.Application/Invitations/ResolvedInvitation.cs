namespace Backroom.Modules.Directory.Application.Invitations;

/// <summary>
/// What the invite page shows before the invitee joins. <paramref name="MatchesAccount"/> is false when the signed-in
/// account's verified email is not the invited one (or not verified), without revealing the invited address.
/// </summary>
internal sealed record ResolvedInvitation(string OrganizationName, DateTimeOffset ExpiresAt, bool MatchesAccount);
