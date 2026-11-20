namespace Backroom.BuildingBlocks.Application.Organizations;

/// <summary>
/// Whether an account may work in the organization of the current request. Implemented by the Organization module;
/// other modules' endpoints reach it through the pipeline's membership check.
/// </summary>
public interface IOrganizationMembershipCheck
{
    /// <summary>Runs in the current unit of work, which row-level security limits to the entered organization.</summary>
    Task<bool> IsActiveMemberAsync(Guid accountId, CancellationToken cancellationToken);
}
