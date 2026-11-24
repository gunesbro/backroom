using Npgsql;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.Modules.Identity.Application;
using Backroom.Modules.Identity.Contracts;

namespace Backroom.Modules.Identity.Infrastructure;

/// <summary>
/// Hand-written SQL; every statement uses the primary key. Reads and writes of one account run in the scope's unit of work; the
/// <c>FullSync</c> list runs on its own connection, because the link's call reads other modules in the same scope.
/// </summary>
internal sealed class AccountStatusStore(DatabaseSession session, NpgsqlDataSource dataSource) : IAccountStatusStore
{
    private const string Columns = "account_id, dnd_until, custom_emoji, custom_text, custom_expires_at, time_zone, version";

    private const string GetSql = $"SELECT {Columns} FROM identity.account_status WHERE account_id = $1";

    // The row lock of the upsert serializes concurrent changes of one account, so versions never repeat.
    private const string SaveSql = $"""
        INSERT INTO identity.account_status AS s ({Columns}, updated_at)
        VALUES ($1, $2, $3, $4, $5, $6, 1, $7)
        ON CONFLICT (account_id) DO UPDATE
            SET dnd_until = EXCLUDED.dnd_until, custom_emoji = EXCLUDED.custom_emoji, custom_text = EXCLUDED.custom_text,
                custom_expires_at = EXCLUDED.custom_expires_at, time_zone = EXCLUDED.time_zone,
                version = s.version + 1, updated_at = EXCLUDED.updated_at
        RETURNING {Columns}
        """;

    private const string ListSql = $"SELECT {Columns} FROM identity.account_status WHERE account_id = ANY($1)";

    private const int MaxListed = 5000;

    public async Task<AccountStatus?> GetAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(GetSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = accountId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? await ReadAsync(reader, cancellationToken) : null;
    }

    public async Task<AccountStatus> SaveAsync(Guid accountId, AccountStatusInput status, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(status);
        await using var command = new NpgsqlCommand(SaveSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = accountId });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset?> { TypedValue = status.DndUntil });
        command.Parameters.Add(new NpgsqlParameter<string?> { TypedValue = status.CustomEmoji });
        command.Parameters.Add(new NpgsqlParameter<string?> { TypedValue = status.CustomText });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset?> { TypedValue = status.CustomExpiresAt });
        command.Parameters.Add(new NpgsqlParameter<string?> { TypedValue = status.TimeZone });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = now });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("The status upsert returned no row.");
        }

        return await ReadAsync(reader, cancellationToken);
    }

    public async Task<IReadOnlyList<AccountStatus>> ListAsync(IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(accountIds);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(accountIds.Count, MaxListed);
        if (accountIds.Count == 0)
        {
            return [];
        }

        await using var command = dataSource.CreateCommand(ListSql);
        command.Parameters.Add(new NpgsqlParameter<Guid[]> { TypedValue = [.. accountIds] });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var statuses = new List<AccountStatus>();
        while (await reader.ReadAsync(cancellationToken))
        {
            statuses.Add(await ReadAsync(reader, cancellationToken));
        }

        return statuses;
    }

    private static async Task<AccountStatus> ReadAsync(NpgsqlDataReader reader, CancellationToken cancellationToken) => new(
        reader.GetGuid(0),
        await reader.IsDBNullAsync(1, cancellationToken) ? null : reader.GetFieldValue<DateTimeOffset>(1),
        await reader.IsDBNullAsync(2, cancellationToken) ? null : reader.GetString(2),
        await reader.IsDBNullAsync(3, cancellationToken) ? null : reader.GetString(3),
        await reader.IsDBNullAsync(4, cancellationToken) ? null : reader.GetFieldValue<DateTimeOffset>(4),
        await reader.IsDBNullAsync(5, cancellationToken) ? null : reader.GetString(5),
        reader.GetInt64(6));
}
