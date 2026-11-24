using System.Diagnostics.CodeAnalysis;
using Npgsql;
using Backroom.ControlPlane.Contracts.V1;
using Backroom.Testing.Postgres;

namespace Backroom.Modules.Identity.IntegrationTests;

/// <summary>Arranges and inspects rows as the superuser (row-level security does not apply to these tables anyway).</summary>
internal sealed class TestData(PostgresDatabase database)
{
    /// <summary>Registers a cell (outbound messages reference it).</summary>
    public Task<string> AddCellAsync() => AddCellCoreAsync("cell-" + Guid.NewGuid().ToString("N")[..12]);

    /// <summary>An active membership of the account in a new organization on <paramref name="cellId"/>.</summary>
    public async Task AddMembershipAsync(Guid accountId, string cellId, string status = "active", string organizationStatus = "active")
    {
        var organization = Guid.CreateVersion7();
        await ExecuteAsync(
            "INSERT INTO directory.organizations (id, name, region, cell_id, status, created_by, created_at) VALUES ($1, 'Acme', 'eu', $2, $3, $4, now())",
            organization, cellId, organizationStatus, accountId);
        await ExecuteAsync(
            "INSERT INTO directory.memberships (account_id, org_id, status, version, updated_at) VALUES ($1, $2, $3, 1, now())",
            accountId, organization, status);
    }

    /// <summary>The <c>SessionRevoked</c> messages queued for the cell, oldest first.</summary>
    public async Task<IReadOnlyList<SessionRevoked>> SessionRevokedMessagesAsync(string cellId)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT payload FROM cell_management.outbound_messages WHERE cell_id = $1 AND type = 'SessionRevoked' ORDER BY sequence", connection);
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = cellId });
        await using var reader = await command.ExecuteReaderAsync();
        var messages = new List<SessionRevoked>();
        while (await reader.ReadAsync())
        {
            messages.Add(ConnectResponse.Parser.ParseFrom((byte[])reader[0]).SessionRevoked);
        }

        return messages;
    }

    public async Task<long> CountRevokedSessionsAsync(Guid accountId) =>
        await ScalarAsync<long>("SELECT count(*) FROM identity.revoked_sessions WHERE account_id = $1", accountId);

    public async Task<(string SessionId, DateTime RevokedAt, DateTime KeepUntil)> RevokedSessionAsync(Guid accountId)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT session_id, revoked_at, keep_until FROM identity.revoked_sessions WHERE account_id = $1", connection);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = accountId });
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetString(0), reader.GetDateTime(1), reader.GetDateTime(2));
    }

    /// <summary>Inserts a revoked session directly (an arranged row, whatever the clock says).</summary>
    public Task AddRevokedSessionAsync(Guid accountId, string sessionId, DateTimeOffset revokedAt, DateTimeOffset keepUntil) =>
        ExecuteAsync(
            "INSERT INTO identity.revoked_sessions (session_id, account_id, revoked_at, keep_until) VALUES ($1, $2, $3, $4)",
            sessionId, accountId, revokedAt, keepUntil);

    /// <summary>Deletes the accounts' revoked sessions, so a test leaves no rows for the cleanup tests to count.</summary>
    public Task DeleteRevokedSessionsAsync(params Guid[] accountIds) =>
        ExecuteAsync("DELETE FROM identity.revoked_sessions WHERE account_id = ANY($1)", accountIds);

    /// <summary>Inserts <paramref name="count"/> revoked sessions of accounts that belong to no cell, and returns their ids.</summary>
    public async Task<Guid[]> AddStrangerRevocationsAsync(int count, DateTimeOffset revokedAt, DateTimeOffset keepUntil)
    {
        var accounts = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray();
        await ExecuteAsync(
            "INSERT INTO identity.revoked_sessions (session_id, account_id, revoked_at, keep_until) SELECT 's-stranger', unnest($1::uuid[]), $2, $3",
            accounts, revokedAt, keepUntil);
        return accounts;
    }

    private async Task<string> AddCellCoreAsync(string id)
    {
        await ExecuteAsync(
            "INSERT INTO cell_management.cells (id, name, type, region, public_url, status, created_at) VALUES ($1, 'Test', 'shared', 'eu-test', 'https://cell.example.test/', 'registered', now())",
            id);
        return id;
    }

    [SuppressMessage("Security", "CA2100", Justification = "Test setup; the SQL is written in the tests.")]
    private async Task ExecuteAsync(string sql, params object[] parameters)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        }

        await command.ExecuteNonQueryAsync();
    }

    [SuppressMessage("Security", "CA2100", Justification = "Test setup; the SQL is written in the tests.")]
    private async Task<T> ScalarAsync<T>(string sql, params object[] parameters)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        }

        return (T)(await command.ExecuteScalarAsync())!;
    }
}
