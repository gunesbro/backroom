using Backroom.BuildingBlocks.Domain.Events;

namespace Backroom.Modules.Organization.Contracts;

/// <summary>
/// An account is no longer an active member (docs/contracts/phase-1.md, "Integration events"). Realtime removes the
/// account's connections from the organization's groups. <paramref name="Version"/> is the membership's version.
/// </summary>
public sealed record MemberRemoved(
    Guid EventId,
    Guid OrgId,
    DateTimeOffset OccurredAt,
    Guid AccountId,
    long Version) : IIntegrationEvent
{
    public static string EventType => "organization.member-removed.v1";
}
