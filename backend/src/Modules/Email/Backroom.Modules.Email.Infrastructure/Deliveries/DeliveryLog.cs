using Npgsql;
using NpgsqlTypes;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.Modules.Email.Application.Deliveries;

namespace Backroom.Modules.Email.Infrastructure.Deliveries;

/// <summary>The delivery log with hand-written SQL in the caller's unit of work (row-level security applies).</summary>
internal sealed class DeliveryLog(DatabaseSession session) : IDeliveryLog
{
    // The conflict target is the partial unique index deliveries_reference_once_idx: a pending, unknown or sent delivery of the
    // same template name for the same reference wins, and nothing is recorded (a concurrent insert waits for it).
    private const string AddSql = """
        INSERT INTO email.deliveries
            (id, org_id, template, template_version, recipient_hash, status, created_at, updated_at, reference_id, reference_version)
        VALUES ($1, $2, $3, $4, $5, 'pending', $6, $6, $7, $8)
        ON CONFLICT (org_id, template, reference_id, reference_version) WHERE reference_id IS NOT NULL AND status <> 'failed'
        DO NOTHING
        """;

    private const string CompleteSql = """
        UPDATE email.deliveries SET status = $2, provider_message_id = $3, updated_at = $4
        WHERE id = $1 AND status IN ('pending', 'unknown')
        """;

    public async Task<bool> AddPendingAsync(
        Guid deliveryId,
        Guid organizationId,
        EmailTemplateId template,
        EmailDeliveryReference? reference,
        byte[] recipientHash,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(recipientHash);
        await using var command = new NpgsqlCommand(AddSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = deliveryId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organizationId });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = template.Name });
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = template.Version });
        command.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = recipientHash });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = now });
        command.Parameters.Add(new NpgsqlParameter { Value = reference is { } withId ? withId.Id : DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
        command.Parameters.Add(new NpgsqlParameter { Value = reference is { } withVersion ? withVersion.Version : DBNull.Value, NpgsqlDbType = NpgsqlDbType.Integer });
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task CompleteAsync(Guid deliveryId, string status, string? providerMessageId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (status is not (DeliveryStatus.Sent or DeliveryStatus.Failed))
        {
            throw new ArgumentException("A delivery completes as sent or failed.", nameof(status));
        }

        await using var command = new NpgsqlCommand(CompleteSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = deliveryId });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = status });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)providerMessageId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = now });
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("The delivery is not pending in this organization.");
        }
    }
}
