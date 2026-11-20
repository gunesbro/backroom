namespace Backroom.Modules.Organization.Presentation;

public sealed record MemberResponse(Guid AccountId, string DisplayName, string? Email);
