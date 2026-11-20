using Backroom.Modules.Organization.Contracts;

namespace Backroom.Modules.Organization.Application.Provisioning;

/// <summary>Organization's tables, in the scope's unit of work (already in the organization).</summary>
internal interface IOrganizationStore
{
    /// <summary>Adds the organization; if it exists already, returns its owner instead (<see langword="null"/> when added now).</summary>
    Task<Guid?> AddOrGetOwnerAsync(NewOrganization organization, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Adds the owner as the organization's first active member.</summary>
    Task AddOwnerAsync(NewOrganization organization, DateTimeOffset now, CancellationToken cancellationToken);
}
