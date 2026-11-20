namespace Backroom.BuildingBlocks.Application.Organizations;

/// <summary>
/// Whether an account holds a permission in the organization of the current request. Implemented by the Permissions
/// module; other modules' endpoints reach it through the pipeline's permission check.
/// </summary>
public interface IOrganizationPermissionCheck
{
    /// <summary>Runs in the current unit of work, which row-level security limits to the entered organization.</summary>
    Task<bool> HasPermissionAsync(Guid accountId, string permission, CancellationToken cancellationToken);
}
