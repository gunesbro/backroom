using Backroom.BuildingBlocks.Infrastructure.Persistence;

namespace Backroom.BuildingBlocks.Infrastructure.Messaging;

/// <summary>
/// The inbox table each consuming module has in its schema: one row per event and handler, written in
/// the handler's transaction, so a redelivered event is recognised and skipped. Rows are organization
/// data (RLS on); the application role may only insert, and the relay role deletes old rows.
/// </summary>
public static class InboxTable
{
    public const string Name = "inbox";

    public static string CreateSql(string schema)
    {
        var table = OrganizationRowLevelSecurity.QualifiedName(schema, Name);
        return $"""
            CREATE TABLE {table} (
                event_id uuid NOT NULL,
                handler text NOT NULL,
                org_id uuid NOT NULL,
                processed_at timestamptz NOT NULL,
                PRIMARY KEY (event_id, handler));
            CREATE INDEX inbox_processed_at_idx ON {table} (processed_at);
            {OrganizationRowLevelSecurity.EnableSql(schema, Name)}
            REVOKE ALL ON {table} FROM {DatabaseRoles.Application};
            GRANT INSERT ON {table} TO {DatabaseRoles.Application};
            GRANT SELECT, DELETE ON {table} TO {DatabaseRoles.Relay};
            """;
    }
}
