using Backroom.BuildingBlocks.Application.Messaging;
using Backroom.Modules.Organization.Contracts;

namespace Backroom.Modules.Realtime.Infrastructure.Gateway;

/// <summary>
/// Worker role: <c>organization.member-removed.v1</c> → every app node removes the account's connections from that
/// organization's groups (<see cref="GroupRemovalBroadcast"/>). Removing is idempotent, so a redelivered event is harmless;
/// when Valkey is down the handler throws and the event is delivered again.
/// </summary>
internal sealed class MemberRemovedConsumer(GroupRemovalBroadcast broadcast) : IIntegrationEventHandler<MemberRemoved>
{
    public Task HandleAsync(MemberRemoved integrationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        return broadcast.PublishAsync(integrationEvent.OrgId, integrationEvent.AccountId, cancellationToken);
    }
}
