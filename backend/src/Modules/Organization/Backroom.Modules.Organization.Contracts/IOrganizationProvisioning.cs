using Backroom.BuildingBlocks.Domain.Errors;

namespace Backroom.Modules.Organization.Contracts;

/// <summary>
/// Creates an organization in this cell (docs/contracts/phase-1.md, flow 2, step 3). Called by
/// ControlPlaneConnector for <c>OrganizationProvisioned</c>, in the unit of work that records the message,
/// which is already in that organization. Idempotent by organization id: provisioning one that exists
/// changes nothing and succeeds. A failure is a business rejection (the control plane fails the
/// organization with its code).
/// </summary>
public interface IOrganizationProvisioning
{
    Task<Result> ProvisionAsync(NewOrganization organization, CancellationToken cancellationToken);
}
