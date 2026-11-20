using Backroom.BuildingBlocks.Infrastructure.Persistence;

namespace Backroom.BuildingBlocks.Infrastructure.Messaging;

/// <summary>
/// The outbox table each publishing module has in its schema. Rows are organization data (RLS on);
/// the application role may only insert, and the relay role reads, marks and deletes them.
/// </summary>
public static class OutboxTable
{
    public const string Name = "outbox";

    /// <summary>
    /// Largest serialized event accepted. Events carry ids, not content, so this is generous; it also
    /// keeps a message far below the broker's maximum message size.
    /// </summary>
    public const int MaxPayloadBytes = 64 * 1024;

    /// <remarks>
    /// <c>failed_at</c> is set by the relay for a row it can never publish (invalid type, wrong module,
    /// oversized payload); such rows are skipped and kept for diagnosis. Clearing <c>failed_at</c>
    /// makes the relay try again.
    /// </remarks>
    public static string CreateSql(string schema)
    {
        var table = OrganizationRowLevelSecurity.QualifiedName(schema, Name);
        return $"""
            CREATE TABLE {table} (
                id uuid PRIMARY KEY,
                org_id uuid NOT NULL,
                event_type text NOT NULL,
                payload bytea NOT NULL,
                trace_parent text,
                trace_state text,
                occurred_at timestamptz NOT NULL,
                published_at timestamptz,
                failed_at timestamptz);
            CREATE INDEX outbox_unpublished_idx ON {table} (id) WHERE published_at IS NULL AND failed_at IS NULL;
            CREATE INDEX outbox_published_at_idx ON {table} (published_at) WHERE published_at IS NOT NULL;
            {OrganizationRowLevelSecurity.EnableSql(schema, Name)}
            REVOKE ALL ON {table} FROM {DatabaseRoles.Application};
            GRANT INSERT ON {table} TO {DatabaseRoles.Application};
            GRANT SELECT, DELETE ON {table} TO {DatabaseRoles.Relay};
            GRANT UPDATE (published_at, failed_at) ON {table} TO {DatabaseRoles.Relay};
            """;
    }
}
