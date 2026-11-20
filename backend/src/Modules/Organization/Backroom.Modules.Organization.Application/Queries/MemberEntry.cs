namespace Backroom.Modules.Organization.Application.Queries;

/// <summary>A member as other members see them; <paramref name="Email"/> only on the reader's own entry.</summary>
internal sealed record MemberEntry(Guid AccountId, string DisplayName, string? Email);
