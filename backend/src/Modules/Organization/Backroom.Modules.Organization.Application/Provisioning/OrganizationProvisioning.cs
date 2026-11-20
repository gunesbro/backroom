using Backroom.BuildingBlocks.Domain.Errors;
using Backroom.Modules.Organization.Contracts;
using Backroom.Modules.Permissions.Contracts;

namespace Backroom.Modules.Organization.Application.Provisioning;

/// <summary>
/// Creates an organization, its owner as the first member and its system roles with Owner assigned, all in
/// the caller's unit of work (docs/contracts/phase-1.md, flow 2, step 3). Idempotent by organization id. The
/// control plane validated the values; they are bounded here again because they cross the link.
/// </summary>
internal sealed class OrganizationProvisioning(IOrganizationStore organizations, IPermissionsProvisioning permissions, TimeProvider timeProvider)
    : IOrganizationProvisioning
{
    public static readonly Error Invalid =
        Error.Validation("organization.provisioning_invalid", "The organization or its owner is not valid.");

    public static readonly Error OwnedByAnother =
        Error.Conflict("organization.exists", "The organization exists with another owner.");

    public async Task<Result> ProvisionAsync(NewOrganization organization, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organization);
        if (!IsValid(organization))
        {
            return Invalid;
        }

        var now = timeProvider.GetUtcNow();
        if (await organizations.AddOrGetOwnerAsync(organization, now, cancellationToken) is { } existingOwner)
        {
            // Sent again (for example after a lost Ack): nothing to do.
            return existingOwner == organization.OwnerAccountId ? Result.Success : OwnedByAnother;
        }

        await organizations.AddOwnerAsync(organization, now, cancellationToken);
        await permissions.InitializeOrganizationAsync(organization.Id, cancellationToken);
        await permissions.AssignRoleAsync(organization.Id, organization.OwnerAccountId, SystemRoles.Owner, cancellationToken);
        return Result.Success;
    }

    // The column limits; the control plane enforces the finer rules.
    private static bool IsValid(NewOrganization organization) =>
        organization.Id != Guid.Empty
        && organization.OwnerAccountId != Guid.Empty
        && ColumnText.Fits(organization.Name, 1, 400)
        && ColumnText.Fits(organization.OwnerDisplayName, 1, 400)
        && ColumnText.Fits(organization.OwnerEmail, 3, 254) && organization.OwnerEmail.Contains('@', StringComparison.Ordinal)
        && ColumnText.Fits(organization.OwnerLocale, 2, 35);
}
