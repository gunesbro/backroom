using Microsoft.EntityFrameworkCore;

namespace Backroom.Modules.Directory.Infrastructure;

/// <summary>The module's schema. Queries are hand-written SQL, so the context has no entities; it owns the migrations.</summary>
internal sealed class DirectoryDbContext(DbContextOptions<DirectoryDbContext> options) : DbContext(options)
{
    public const string Schema = "directory";
}
