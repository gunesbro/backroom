using Backroom.ControlPlane.Contracts.V1;
using Backroom.Modules.Organization.Contracts;

namespace Backroom.Modules.ControlPlaneConnector.Infrastructure.Link;

/// <summary>
/// <c>OrganizationProvisioned</c> → <see cref="IOrganizationProvisioning"/> (docs/contracts/phase-1.md, flow 2,
/// step 3), in the receiver's unit of work, which is already in that organization. A business rejection is a
/// non-retryable <c>Nack</c> with Organization's code; the control plane then fails the organization.
/// </summary>
internal sealed class OrganizationProvisionedHandler(IOrganizationProvisioning provisioning) : IDownstreamHandler<OrganizationProvisioned>
{
    public async Task HandleAsync(Guid messageId, OrganizationProvisioned message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!Guid.TryParse(message.OrganizationId, out var organizationId) || !Guid.TryParse(message.OwnerAccountId, out var ownerId))
        {
            throw new LinkMessageRejectedException("organization.provisioning_invalid");
        }

        var result = await provisioning.ProvisionAsync(
            new NewOrganization(organizationId, message.Name, ownerId, message.OwnerDisplayName, message.OwnerEmail, message.OwnerLocale),
            cancellationToken);
        if (result.Error is { } error)
        {
            throw new LinkMessageRejectedException(error.Code);
        }
    }
}
