using Npgsql;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.Modules.Messaging.Application.Channels;

namespace Backroom.Modules.Messaging.Infrastructure;

/// <summary>Channels; row-level security limits every statement to the entered organization, and each query also names it so the indexes lead with it.</summary>
internal sealed class ChannelStore(DatabaseSession session) : IChannelStore
{
    private const string AddSql = """
        INSERT INTO messaging.channels (id, org_id, name, name_key, created_by, created_at)
        VALUES ($1, $2, $3, $4, $5, $6)
        ON CONFLICT (org_id, name_key) WHERE archived_at IS NULL DO NOTHING
        """;

    private const string ListSql = """
        SELECT id, name, created_at
        FROM messaging.channels
        WHERE org_id = $1 AND archived_at IS NULL AND id > $2
        ORDER BY id
        LIMIT $3
        """;

    private const string ExistsSql = "SELECT 1 FROM messaging.channels WHERE org_id = $1 AND id = $2 AND archived_at IS NULL";

    // The row lock this takes is held until the transaction ends.
    private const string NextSequenceSql =
        "UPDATE messaging.channels SET last_seq = last_seq + 1 WHERE org_id = $1 AND id = $2 AND archived_at IS NULL RETURNING last_seq";

    public async Task<bool> AddAsync(NewChannel channel, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(AddSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = channel.Id });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = channel.OrgId });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = channel.Name });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = channel.NameKey });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = channel.CreatedBy });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = channel.CreatedAt });
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<IReadOnlyList<ChannelEntry>> ListAsync(Guid organizationId, Guid after, int count, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(ListSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organizationId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = after });
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = count });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var channels = new List<ChannelEntry>(Math.Min(count, 101));
        while (await reader.ReadAsync(cancellationToken))
        {
            channels.Add(new ChannelEntry(reader.GetGuid(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2)));
        }

        return channels;
    }

    public async Task<bool> ExistsAsync(Guid organizationId, Guid channelId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(ExistsSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organizationId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = channelId });
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public async Task<long?> NextSequenceAsync(Guid organizationId, Guid channelId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(NextSequenceSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organizationId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = channelId });
        return await command.ExecuteScalarAsync(cancellationToken) is long seq ? seq : null;
    }
}
