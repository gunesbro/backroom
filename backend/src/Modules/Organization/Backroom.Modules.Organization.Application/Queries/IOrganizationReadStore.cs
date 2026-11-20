namespace Backroom.Modules.Organization.Application.Queries;

/// <summary>Reads in the current unit of work, which row-level security limits to the entered organization.</summary>
internal interface IOrganizationReadStore
{
    Task<OrganizationSummary?> GetAsync(Guid organizationId, CancellationToken cancellationToken);

    /// <summary>Active members after <paramref name="after"/> by account id, at most <paramref name="count"/>.</summary>
    Task<IReadOnlyList<MemberEntry>> ListMembersAsync(Guid readerAccountId, Guid after, int count, CancellationToken cancellationToken);
}
