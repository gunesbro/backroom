using Microsoft.EntityFrameworkCore;

namespace Backroom.Modules.Realtime.Infrastructure;

/// <summary>The module's organization-scoped schema: only the inbox of its event consumer. It has no entities; it owns the migrations.</summary>
internal sealed class RealtimeDbContext(DbContextOptions<RealtimeDbContext> options) : DbContext(options)
{
    public const string Schema = "realtime";
}
