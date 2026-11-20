using Microsoft.EntityFrameworkCore;

namespace Backroom.Modules.ControlPlaneConnector.Infrastructure;

/// <summary>
/// The connector's schema (link inbox and outbox). Link messages span organizations (a digest names many),
/// so the schema is not organization-scoped. Pending payloads can carry organization data (an invitee's
/// email, a name), so the application role may only insert; the relay role reads and settles.
/// Queries are hand-written SQL, so the context has no entities; it owns the migrations.
/// </summary>
internal sealed class ControlPlaneConnectorDbContext(DbContextOptions<ControlPlaneConnectorDbContext> options) : DbContext(options)
{
    public const string Schema = "control_plane_connector";
}
