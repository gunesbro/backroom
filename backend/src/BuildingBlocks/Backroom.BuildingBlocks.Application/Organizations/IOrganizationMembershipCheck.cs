namespace Backroom.BuildingBlocks.Application.Organizations;

/// <summary>
/// Whether an account may work in the organization of the current request. Implemented by the Organization module;
/// other modules' endpoints reach it through the pipeline's membership check.
/// </summary>
public interface IOrganizationMembershipCheck
{
    /// <summary>Runs in the current unit of work, which row-level security limits to the entered organization.</summary>
    Task<bool> IsActiveMemberAsync(Guid accountId, CancellationToken cancellationToken);

    /// <summary>The most accounts <see cref="SelectActiveMembersAsync"/> checks at once.</summary>
    public const int MaxAccountsPerSelection = 1000;

    /// <summary>
    /// The accounts among <paramref name="accountIds"/> (at most <see cref="MaxAccountsPerSelection"/>) that are active members of the
    /// entered organization, in one statement; runs in the current unit of work like <see cref="IsActiveMemberAsync"/>.
    /// </summary>
    Task<IReadOnlyList<Guid>> SelectActiveMembersAsync(IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken);
}
