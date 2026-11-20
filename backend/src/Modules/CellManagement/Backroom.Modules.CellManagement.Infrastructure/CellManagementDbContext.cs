using Microsoft.EntityFrameworkCore;

namespace Backroom.Modules.CellManagement.Infrastructure;

/// <summary>
/// The module's schema. The registry uses hand-written SQL (small, security-sensitive statements),
/// so the context has no entities; it owns the migrations.
/// </summary>
internal sealed class CellManagementDbContext(DbContextOptions<CellManagementDbContext> options) : DbContext(options)
{
    public const string Schema = "cell_management";
}
