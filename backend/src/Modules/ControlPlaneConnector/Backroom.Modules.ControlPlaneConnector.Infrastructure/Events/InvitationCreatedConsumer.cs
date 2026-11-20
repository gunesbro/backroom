using System.Globalization;
using Google.Protobuf.WellKnownTypes;
using Backroom.BuildingBlocks.Application.Messaging;
using Backroom.ControlPlane.Contracts.V1;
using Backroom.Modules.ControlPlaneConnector.Infrastructure.Link;
using Backroom.Modules.Organization.Contracts;

namespace Backroom.Modules.ControlPlaneConnector.Infrastructure.Events;

/// <summary>
/// <c>organization.invitation-created.v1</c> → upstream <c>InvitationRegistered</c> (docs/contracts/phase-1.md, flow 3,
/// step 2), in the event's unit of work, which is in its organization. The hash is read for the event's code version;
/// if a resend replaced it or the invitation is no longer pending, nothing is sent (a newer event or the revocation
/// follows). The code itself never leaves Organization.
/// </summary>
internal sealed class InvitationCreatedConsumer(IInvitationLinks links, UpstreamOutbox outbox) : IIntegrationEventHandler<InvitationCreated>
{
    public async Task HandleAsync(InvitationCreated integrationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        if (await links.GetCodeHashAsync(integrationEvent.InvitationId, integrationEvent.CodeVersion, cancellationToken) is not { } codeHash)
        {
            return;
        }

        await outbox.EnqueueAsync(
            new ConnectRequest
            {
                InvitationRegistered = new InvitationRegistered
                {
                    OrganizationId = integrationEvent.OrgId.ToString("D", CultureInfo.InvariantCulture),
                    InvitationId = integrationEvent.InvitationId.ToString("D", CultureInfo.InvariantCulture),
                    CodeHash = codeHash,
                    Email = integrationEvent.Email,
                    Role = integrationEvent.Role,
                    ExpiresAt = Timestamp.FromDateTimeOffset(integrationEvent.ExpiresAt),
                    CodeVersion = checked((uint)integrationEvent.CodeVersion),
                },
            },
            cancellationToken);
    }
}
