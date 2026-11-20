using Backroom.BuildingBlocks.Application.Organizations;
using Backroom.BuildingBlocks.Domain.Errors;

namespace Backroom.Modules.Organization.Application.Queries;

/// <summary>The organization a member is working in; the pipeline has checked the membership.</summary>
internal sealed class OrganizationQueries(IOrganizationReadStore store, IOrganizationContext context)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;

    private static readonly Error s_notFound = Error.NotFound("organization.not_found", "The organization was not found.");
    private static readonly Error s_pageInvalid = Error.Validation("organization.page_invalid", "The page size must be between 1 and 100.");

    public async Task<Result<OrganizationSummary>> GetAsync(CancellationToken cancellationToken)
    {
        var organization = await store.GetAsync(OrganizationId, cancellationToken);
        return organization is null ? s_notFound : organization;
    }

    public async Task<Result<MemberPage>> ListMembersAsync(Guid readerAccountId, Guid? after, int? limit, CancellationToken cancellationToken)
    {
        var pageSize = limit ?? DefaultPageSize;
        if (pageSize is < 1 or > MaxPageSize)
        {
            return s_pageInvalid;
        }

        // One more than asked for tells whether another page follows.
        var members = await store.ListMembersAsync(readerAccountId, after ?? Guid.Empty, pageSize + 1, cancellationToken);
        return members.Count > pageSize
            ? new MemberPage([.. members.Take(pageSize)], members[pageSize - 1].AccountId)
            : new MemberPage(members, null);
    }

    private Guid OrganizationId =>
        context.OrganizationId ?? throw new InvalidOperationException("The request has not entered an organization.");
}
