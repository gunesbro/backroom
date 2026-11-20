using Backroom.BuildingBlocks.Domain.Events;

namespace Backroom.Modules.Organization.Contracts;

/// <summary>
/// An account became an active member with <paramref name="Role"/> (docs/contracts/phase-1.md, "Integration events").
/// <paramref name="Version"/> is the membership's version, incremented on every change, so consumers apply only newer
/// ones. It carries no name or email.
/// </summary>
public sealed record MemberAdded(
    Guid EventId,
    Guid OrgId,
    DateTimeOffset OccurredAt,
    Guid AccountId,
    string Role,
    long Version) : IIntegrationEvent
{
    public static string EventType => "organization.member-added.v1";
}
