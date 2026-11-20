using Backroom.BuildingBlocks.Domain.Events;

namespace Backroom.Modules.Organization.Contracts;

/// <summary>A pending invitation was revoked; its link no longer works (docs/contracts/phase-1.md, flow 3).</summary>
public sealed record InvitationRevoked(Guid EventId, Guid OrgId, DateTimeOffset OccurredAt, Guid InvitationId) : IIntegrationEvent
{
    public static string EventType => "organization.invitation-revoked.v1";
}
