namespace Backroom.Modules.Organization.Presentation;

public sealed record MembersResponse(IReadOnlyList<MemberResponse> Members, Guid? Next);
