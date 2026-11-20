using Backroom.ControlPlane.Contracts.V1;
using Backroom.Modules.CellManagement.Contracts;

namespace Backroom.Modules.Directory.Application.Organizations;

/// <summary>
/// The cell's answer to <c>OrganizationProvisioned</c> (docs/contracts/phase-1.md, flow 2, step 4): <c>Ack</c>
/// makes the organization and its owner's membership active; a non-retryable <c>Nack</c> fails the
/// organization with the cell's code. Only the cell the organization was assigned to can settle it.
/// </summary>
internal sealed class OrganizationProvisioningOutcome(IOrganizationStore organizations, TimeProvider timeProvider)
    : ICellMessageOutcomeHandler<OrganizationProvisioned>
{
    public async Task OnAcknowledgedAsync(CellMessage<OrganizationProvisioned> message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (Guid.TryParse(message.Payload.OrganizationId, out var organizationId))
        {
            await organizations.ActivateAsync(organizationId, message.CellId, timeProvider.GetUtcNow(), cancellationToken);
        }
    }

    public async Task OnRejectedAsync(CellMessage<OrganizationProvisioned> message, string code, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (Guid.TryParse(message.Payload.OrganizationId, out var organizationId))
        {
            await organizations.FailAsync(organizationId, message.CellId, code, timeProvider.GetUtcNow(), cancellationToken);
        }
    }
}
