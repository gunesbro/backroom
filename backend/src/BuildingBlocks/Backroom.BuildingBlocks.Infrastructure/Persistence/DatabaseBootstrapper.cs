using Npgsql;

namespace Backroom.BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// Development only: creates the four database roles with their passwords and gives them access to
/// the database, as an administrator. Idempotent and safe to run concurrently; on later runs it
/// updates the passwords and attributes. Deployments provision the roles themselves and never run this.
/// </summary>
public static class DatabaseBootstrapper
{
    // This session's statements and their parameters are not written to the server log.
    private const string QuietLoggingSql = "SET log_statement = 'none'; SET log_parameter_max_length = 0; SET log_min_error_statement = 'panic'";

    // The passwords travel as parameters into session settings and are read by format(%L) inside the
    // block, so no password is part of the SQL text; errors are re-raised without the failed statement.
    private const string StorePasswordsSql = """
        SELECT set_config('backroom.migrator_password', $1, false),
               set_config('backroom.application_password', $2, false),
               set_config('backroom.relay_password', $3, false),
               set_config('backroom.maintenance_password', $4, false)
        """;

    // Role-level timeouts of the maintenance role: a statement may run 30 s, waiting for a lock gives up
    // after 5 s and an open transaction that does nothing is ended after 60 s.
    internal const string MaintenanceStatementTimeout = "30s";
    internal const string MaintenanceLockTimeout = "5s";
    internal const string MaintenanceIdleInTransactionTimeout = "60s";

    private const string CreateRolesSql = $$"""
        DO $bootstrap$
        DECLARE
            role record;
        BEGIN
            -- Concurrent bootstraps (several migrate runs on one server) take turns.
            PERFORM pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtext('backroom.bootstrap'));

            FOR role IN
                SELECT * FROM (VALUES
                    ('{{DatabaseRoles.Migrator}}', 'backroom.migrator_password', 'NOBYPASSRLS INHERIT'),
                    ('{{DatabaseRoles.Application}}', 'backroom.application_password', 'NOBYPASSRLS NOINHERIT'),
                    ('{{DatabaseRoles.Relay}}', 'backroom.relay_password', 'BYPASSRLS NOINHERIT'),
                    ('{{DatabaseRoles.Maintenance}}', 'backroom.maintenance_password', 'BYPASSRLS NOINHERIT')
                ) AS roles(name, setting, attributes)
            LOOP
                BEGIN
                    EXECUTE format(
                        CASE WHEN EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = role.name)
                             THEN 'ALTER' ELSE 'CREATE' END
                        || ' ROLE %I LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE ' || role.attributes || ' PASSWORD %L',
                        role.name, current_setting(role.setting));
                EXCEPTION WHEN OTHERS THEN
                    -- The original error's context would contain the statement with the password.
                    RAISE EXCEPTION 'Creating or updating role % failed: %', role.name, SQLERRM USING ERRCODE = SQLSTATE;
                END;
            END LOOP;

            -- Maintenance jobs give up and retry in their next run instead of queueing behind a lock
            -- (for example a migration's ALTER TABLE) and never hold locks while idle (docs/adr/0010).
            EXECUTE 'ALTER ROLE {{DatabaseRoles.Maintenance}} SET statement_timeout = ''{{MaintenanceStatementTimeout}}''';
            EXECUTE 'ALTER ROLE {{DatabaseRoles.Maintenance}} SET lock_timeout = ''{{MaintenanceLockTimeout}}''';
            EXECUTE 'ALTER ROLE {{DatabaseRoles.Maintenance}} SET idle_in_transaction_session_timeout = ''{{MaintenanceIdleInTransactionTimeout}}''';

            -- Only the four roles may connect; PUBLIC gets neither CONNECT nor TEMP.
            EXECUTE format('REVOKE ALL ON DATABASE %I FROM PUBLIC', current_database());
            EXECUTE format('GRANT CONNECT, CREATE ON DATABASE %I TO {{DatabaseRoles.Migrator}}', current_database());
            EXECUTE format('GRANT CONNECT ON DATABASE %I TO {{DatabaseRoles.Application}}, {{DatabaseRoles.Relay}}, {{DatabaseRoles.Maintenance}}', current_database());
            REVOKE CREATE ON SCHEMA public FROM PUBLIC;
        END
        $bootstrap$;
        """;

    public static async Task BootstrapAsync(
        string administratorConnectionString,
        string migratorPassword,
        string applicationPassword,
        string relayPassword,
        string maintenancePassword,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(administratorConnectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(migratorPassword);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationPassword);
        ArgumentException.ThrowIfNullOrWhiteSpace(relayPassword);
        ArgumentException.ThrowIfNullOrWhiteSpace(maintenancePassword);

        // Unpooled: the session settings holding the passwords end with this connection.
        var connectionString = new NpgsqlConnectionStringBuilder(administratorConnectionString) { Pooling = false, IncludeErrorDetail = false };
        await using var connection = new NpgsqlConnection(connectionString.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using (var quiet = new NpgsqlCommand(QuietLoggingSql, connection))
        {
            await quiet.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var store = new NpgsqlCommand(StorePasswordsSql, connection))
        {
            store.Parameters.Add(new NpgsqlParameter<string> { TypedValue = migratorPassword });
            store.Parameters.Add(new NpgsqlParameter<string> { TypedValue = applicationPassword });
            store.Parameters.Add(new NpgsqlParameter<string> { TypedValue = relayPassword });
            store.Parameters.Add(new NpgsqlParameter<string> { TypedValue = maintenancePassword });
            await store.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var create = new NpgsqlCommand(CreateRolesSql, connection);
        await create.ExecuteNonQueryAsync(cancellationToken);
    }
}
