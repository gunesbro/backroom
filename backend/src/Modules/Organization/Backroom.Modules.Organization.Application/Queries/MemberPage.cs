namespace Backroom.Modules.Organization.Application.Queries;

/// <summary>One page of members; <paramref name="Next"/> is where the following page starts, if there is one.</summary>
internal sealed record MemberPage(IReadOnlyList<MemberEntry> Members, Guid? Next);
