using System.Globalization;
using Backroom.BuildingBlocks.Application.Messaging;
using Backroom.ControlPlane.Contracts.V1;
using Backroom.Modules.ControlPlaneConnector.Infrastructure.Link;
using InvitationRevoked = Backroom.Modules.Organization.Contracts.InvitationRevoked;

namespace Backroom.Modules.ControlPlaneConnector.Infrastructure.Events;

/// <summary>
/// <c>organization.invitation-revoked.v1</c> → upstream <c>InvitationRevoked</c> (docs/contracts/phase-1.md, flow 3,
/// step 8). Always sent, even if the registration never went out: the control plane then keeps a tombstone, so a
/// late registration cannot bring the invitation back.
/// </summary>
internal sealed class InvitationRevokedConsumer(UpstreamOutbox outbox) : IIntegrationEventHandler<InvitationRevoked>
{
    public async Task HandleAsync(InvitationRevoked integrationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        await outbox.EnqueueAsync(
            new ConnectRequest
            {
                InvitationRevoked = new ControlPlane.Contracts.V1.InvitationRevoked
                {
                    OrganizationId = integrationEvent.OrgId.ToString("D", CultureInfo.InvariantCulture),
                    InvitationId = integrationEvent.InvitationId.ToString("D", CultureInfo.InvariantCulture),
                },
            },
            cancellationToken);
    }
}
