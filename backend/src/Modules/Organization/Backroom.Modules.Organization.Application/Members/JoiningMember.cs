namespace Backroom.Modules.Organization.Application.Members;

/// <summary>An account joining the organization, with its organization-specific profile.</summary>
internal sealed record JoiningMember(
    Guid OrganizationId,
    Guid AccountId,
    string DisplayName,
    string Email,
    string Locale,
    DateTimeOffset JoinedAt);
