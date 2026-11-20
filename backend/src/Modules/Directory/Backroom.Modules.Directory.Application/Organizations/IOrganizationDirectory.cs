namespace Backroom.Modules.Directory.Application.Organizations;

/// <summary>Directory's organizations and memberships (control plane; no organization content).</summary>
internal interface IOrganizationDirectory
{
    /// <summary>The account's active memberships in active organizations, oldest first, at most <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<MemberOrganization>> ListActiveOrganizationsAsync(Guid accountId, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// The organization's state if <paramref name="accountId"/> is a pending or active member, or created it
    /// and it is still provisioning or failed;
    /// otherwise <see langword="null"/>, as for an unknown organization.
    /// </summary>
    Task<OrganizationState?> FindVisibleStateAsync(Guid organizationId, Guid accountId, CancellationToken cancellationToken);
}
