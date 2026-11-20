using Microsoft.EntityFrameworkCore;

namespace Backroom.Modules.Organization.Infrastructure;

/// <summary>The module's organization-scoped schema. Queries are hand-written SQL, so the context has no entities; it owns the migrations.</summary>
internal sealed class OrganizationDbContext(DbContextOptions<OrganizationDbContext> options) : DbContext(options)
{
    public const string Schema = "organization";
}
