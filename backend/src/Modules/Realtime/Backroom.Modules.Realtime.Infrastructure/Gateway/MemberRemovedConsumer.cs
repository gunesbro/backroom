using Backroom.BuildingBlocks.Application.Messaging;
using Backroom.Modules.Organization.Contracts;
using Backroom.Modules.Presence.Contracts;

namespace Backroom.Modules.Realtime.Infrastructure.Gateway;

/// <summary>
/// Worker role: <c>organization.member-removed.v1</c> → the account's presence stops reaching that organization's presence groups,
/// and every app node removes the account's connections from that organization's groups (<see cref="GroupRemovalBroadcast"/>),
/// presence groups included. Both are idempotent, so a redelivered event is harmless; when Valkey is down the handler throws and the
/// event is delivered again.
/// </summary>
internal sealed class MemberRemovedConsumer(GroupRemovalBroadcast broadcast, IPresenceMemberships presence) : IIntegrationEventHandler<MemberRemoved>
{
    public async Task HandleAsync(MemberRemoved integrationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        await presence.ForgetAsync(integrationEvent.OrgId, integrationEvent.AccountId, cancellationToken);
        await broadcast.PublishAsync(integrationEvent.OrgId, integrationEvent.AccountId, cancellationToken);
    }
}
