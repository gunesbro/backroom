using Microsoft.EntityFrameworkCore;

namespace Backroom.Modules.Notifications.Infrastructure;

/// <summary>The module's organization-scoped schema. Queries are hand-written SQL, so the context has no entities; it owns the migrations.</summary>
internal sealed class NotificationsDbContext(DbContextOptions<NotificationsDbContext> options) : DbContext(options)
{
    public const string Schema = "notifications";
}
