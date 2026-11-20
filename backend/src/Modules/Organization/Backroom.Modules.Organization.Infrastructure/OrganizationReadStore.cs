using Npgsql;
using Backroom.BuildingBlocks.Application.Organizations;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.Modules.Organization.Application.Queries;

namespace Backroom.Modules.Organization.Infrastructure;

/// <summary>Reads for a member's requests; row-level security limits every statement to the entered organization.</summary>
internal sealed class OrganizationReadStore(DatabaseSession session) : IOrganizationReadStore, IOrganizationMembershipCheck
{
    private const string IsActiveMemberSql = "SELECT 1 FROM organization.members WHERE account_id = $1 AND status = 'active'";

    private const string GetSql = "SELECT id, name FROM organization.organizations WHERE id = $1";

    // Other people's emails are not even read: the database returns only the reader's own.
    private const string ListMembersSql = """
        SELECT account_id, display_name, CASE WHEN account_id = $1 THEN email END
        FROM organization.members
        WHERE status = 'active' AND account_id > $2
        ORDER BY account_id
        LIMIT $3
        """;

    public async Task<bool> IsActiveMemberAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(IsActiveMemberSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = accountId });
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public async Task<OrganizationSummary?> GetAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(GetSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organizationId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? new OrganizationSummary(reader.GetGuid(0), reader.GetString(1)) : null;
    }

    public async Task<IReadOnlyList<MemberEntry>> ListMembersAsync(Guid readerAccountId, Guid after, int count, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(ListMembersSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = readerAccountId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = after });
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = count });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var members = new List<MemberEntry>(count);
        while (await reader.ReadAsync(cancellationToken))
        {
            var email = await reader.IsDBNullAsync(2, cancellationToken) ? null : reader.GetString(2);
            members.Add(new MemberEntry(reader.GetGuid(0), reader.GetString(1), email));
        }

        return members;
    }
}
