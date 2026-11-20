using Microsoft.Extensions.Logging;
using Backroom.BuildingBlocks.Application.Accounts;
using Backroom.BuildingBlocks.Domain.Errors;
using Backroom.Modules.CellManagement.Contracts;

namespace Backroom.Modules.Directory.Application.Organizations;

/// <summary>The signed-in account's organizations and their provisioning state (docs/contracts/phase-1.md, flows 1 and 2).</summary>
internal sealed partial class OrganizationQueries(
    IOrganizationDirectory directory, ICellCatalog cells, ICurrentAccount account, ILogger<OrganizationQueries> logger)
{
    /// <summary>The most organizations listed for one account; the client needs no paging below it.</summary>
    public const int MaxOrganizations = ICellCatalog.MaxCellsPerLookup;

    public async Task<IReadOnlyList<AccountOrganization>> ListMineAsync(CancellationToken cancellationToken)
    {
        var organizations = await directory.ListActiveOrganizationsAsync(account.AccountId, MaxOrganizations, cancellationToken);
        if (organizations.Count == 0)
        {
            return [];
        }

        var cellIds = organizations.Select(organization => organization.CellId).ToHashSet(StringComparer.Ordinal);
        var addresses = await cells.GetRegisteredCellsAsync(cellIds, cancellationToken);

        // An active organization always maps to a registered cell; one that does not is left out rather
        // than sent without an address, and logged, because it points at broken registry data.
        var result = new List<AccountOrganization>(organizations.Count);
        foreach (var organization in organizations)
        {
            if (addresses.TryGetValue(organization.CellId, out var address))
            {
                result.Add(new AccountOrganization(organization.Id, organization.Name, organization.CellId, address.PublicUrl));
            }
            else
            {
                LogCellNotRegistered(logger, organization.Id, organization.CellId);
            }
        }

        return result;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Active organization {OrganizationId} maps to cell {CellId}, which is not registered; left out of the list.")]
    private static partial void LogCellNotRegistered(ILogger logger, Guid organizationId, string cellId);

    public async Task<Result<OrganizationState>> GetStateAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        var state = await directory.FindVisibleStateAsync(organizationId, account.AccountId, cancellationToken);
        return state is null ? DirectoryErrors.OrganizationNotFound : state;
    }
}
