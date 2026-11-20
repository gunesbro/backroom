using Npgsql;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.Modules.Organization.Application.Members;

namespace Backroom.Modules.Organization.Infrastructure;

/// <summary>Members with hand-written SQL in the scope's unit of work; row-level security limits every statement to the entered organization.</summary>
internal sealed class MemberStore(DatabaseSession session) : IMemberStore
{
    private const string StatusSql = "SELECT status FROM organization.members WHERE account_id = $1";

    // A new member starts at version 1; a removed one comes back with the new profile and the next version. Active
    // and deactivated members are left alone (no row returned).
    private const string JoinSql = """
        INSERT INTO organization.members AS m (org_id, account_id, display_name, email, locale, status, version, joined_at)
        VALUES ($1, $2, $3, $4, $5, 'active', 1, $6)
        ON CONFLICT (org_id, account_id) DO UPDATE
        SET display_name = EXCLUDED.display_name, email = EXCLUDED.email, locale = EXCLUDED.locale,
            status = 'active', version = m.version + 1, joined_at = EXCLUDED.joined_at
        WHERE m.status = 'removed'
        RETURNING m.version
        """;

    public async Task<string?> GetStatusAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(StatusSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = accountId });
        return (string?)await command.ExecuteScalarAsync(cancellationToken);
    }

    public async Task<long?> JoinAsync(JoiningMember member, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(member);
        await using var command = new NpgsqlCommand(JoinSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = member.OrganizationId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = member.AccountId });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = member.DisplayName });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = member.Email });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = member.Locale });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = member.JoinedAt });
        return (long?)await command.ExecuteScalarAsync(cancellationToken);
    }
}
