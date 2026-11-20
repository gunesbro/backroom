using Npgsql;
using Backroom.Modules.Directory.Application.Organizations;

namespace Backroom.Modules.Directory.Infrastructure;

/// <summary>Directory's reads, one statement each, filtered by primary keys or their prefix.</summary>
internal sealed class OrganizationDirectory(NpgsqlDataSource dataSource) : IOrganizationDirectory
{
    private const string ListSql = """
        SELECT o.id, o.name, o.cell_id
        FROM directory.memberships m
        JOIN directory.organizations o ON o.id = m.org_id
        WHERE m.account_id = $1 AND m.status = 'active' AND o.status = 'active'
        ORDER BY o.created_at, o.id
        LIMIT $2
        """;

    private const string StateSql = """
        SELECT o.status, o.failure_code
        FROM directory.organizations o
        WHERE o.id = $1
          AND ((o.created_by = $2 AND o.status IN ('provisioning', 'failed'))
               OR EXISTS (SELECT 1 FROM directory.memberships m
                          WHERE m.account_id = $2 AND m.org_id = o.id AND m.status IN ('pending', 'active')))
        """;

    public async Task<IReadOnlyList<MemberOrganization>> ListActiveOrganizationsAsync(Guid accountId, int limit, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(ListSql);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = accountId });
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = limit });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var organizations = new List<MemberOrganization>();
        while (await reader.ReadAsync(cancellationToken))
        {
            organizations.Add(new MemberOrganization(reader.GetGuid(0), reader.GetString(1), reader.GetString(2)));
        }

        return organizations;
    }

    public async Task<OrganizationState?> FindVisibleStateAsync(Guid organizationId, Guid accountId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(StateSql);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organizationId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = accountId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var failureCode = await reader.IsDBNullAsync(1, cancellationToken) ? null : reader.GetString(1);
        return new OrganizationState(organizationId, reader.GetString(0), failureCode);
    }
}
