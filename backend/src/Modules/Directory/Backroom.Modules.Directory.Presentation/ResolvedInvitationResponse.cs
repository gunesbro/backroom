namespace Backroom.Modules.Directory.Presentation;

/// <summary>The invite page's view of an invitation: no inviter and no invited address.</summary>
public sealed record ResolvedInvitationResponse(string OrganizationName, DateTimeOffset ExpiresAt, bool MatchesAccount);
