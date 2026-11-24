using Npgsql;
using Backroom.Modules.Directory.Contracts;

namespace Backroom.Modules.Directory.Infrastructure;

/// <summary>
/// <see cref="IDirectoryQueries"/>: one bounded statement each, on their own connection (not the caller's unit of work). A
/// membership counts while it is pending or active, so a person who is just joining is already told about a sign-out.
/// </summary>
internal sealed class DirectoryQueries(NpgsqlDataSource dataSource) : IDirectoryQueries
{
    private const string CellsOfAccountSql = """
        SELECT DISTINCT o.cell_id
        FROM directory.memberships m
        JOIN directory.organizations o ON o.id = m.org_id
        WHERE m.account_id = $1 AND m.status IN ('pending', 'active') AND o.status <> 'failed'
        ORDER BY o.cell_id
        LIMIT $2
        """;

    private const string MembersOfCellSql = """
        SELECT DISTINCT m.account_id
        FROM directory.memberships m
        JOIN directory.organizations o ON o.id = m.org_id
        WHERE m.account_id = ANY($1) AND o.cell_id = $2 AND m.status IN ('pending', 'active') AND o.status <> 'failed'
        """;

    private const string OrganizationsOfCellSql = """
        SELECT id, name
        FROM directory.organizations
        WHERE cell_id = $1 AND status <> 'failed'
        ORDER BY created_at, id
        LIMIT $2
        """;

    public async Task<IReadOnlyList<string>> ListCellsOfAccountAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(CellsOfAccountSql);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = accountId });
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = IDirectoryQueries.MaxCellsPerAccount });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var cells = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            cells.Add(reader.GetString(0));
        }

        return cells;
    }

    public async Task<IReadOnlyList<Guid>> SelectMembersOfCellAsync(string cellId, IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(cellId);
        ArgumentNullException.ThrowIfNull(accountIds);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(accountIds.Count, IDirectoryQueries.MaxAccountsPerSelection);
        if (accountIds.Count == 0)
        {
            return [];
        }

        await using var command = dataSource.CreateCommand(MembersOfCellSql);
        command.Parameters.Add(new NpgsqlParameter<Guid[]> { TypedValue = [.. accountIds] });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = cellId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var members = new List<Guid>();
        while (await reader.ReadAsync(cancellationToken))
        {
            members.Add(reader.GetGuid(0));
        }

        return members;
    }

    public async Task<IReadOnlyList<CellOrganization>> ListOrganizationsOfCellAsync(string cellId, int limit, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(cellId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        await using var command = dataSource.CreateCommand(OrganizationsOfCellSql);
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = cellId });
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = limit });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var organizations = new List<CellOrganization>();
        while (await reader.ReadAsync(cancellationToken))
        {
            organizations.Add(new CellOrganization(reader.GetGuid(0), reader.GetString(1)));
        }

        return organizations;
    }
}
