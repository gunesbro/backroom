using Backroom.BuildingBlocks.Domain.Events;

namespace Backroom.Modules.Organization.Contracts;

/// <summary>
/// An invitation was created, or sent again with a new <paramref name="CodeVersion"/> (docs/contracts/phase-1.md,
/// flow 3). It never carries the code or its hash: the consumer reads the hash through <c>IInvitationLinks</c>.
/// </summary>
public sealed record InvitationCreated(
    Guid EventId,
    Guid OrgId,
    DateTimeOffset OccurredAt,
    Guid InvitationId,
    int CodeVersion,
    string Email,
    string Role,
    DateTimeOffset ExpiresAt) : IIntegrationEvent
{
    public static string EventType => "organization.invitation-created.v1";
}
