using Microsoft.EntityFrameworkCore;

namespace Backroom.Modules.Identity.Infrastructure;

/// <summary>The module's schema. Queries are hand-written SQL, so the context has no entities; it owns the migrations.</summary>
internal sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options) : DbContext(options)
{
    public const string Schema = "identity";
}
