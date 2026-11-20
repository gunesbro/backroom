using Npgsql;
using Backroom.Modules.CellManagement.Contracts;

namespace Backroom.Modules.Directory.Infrastructure;

/// <summary>Directory's organization → cell mapping for the link's upstream scope check (one primary-key query).</summary>
internal sealed class CellOrganizationScope(NpgsqlDataSource dataSource) : ICellOrganizationScope
{
    private const string Sql = "SELECT count(*) FROM directory.organizations WHERE id = ANY($1) AND cell_id = $2";

    public async Task<bool> AreInCellAsync(IReadOnlyCollection<Guid> organizationIds, string cellId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organizationIds);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(organizationIds.Count, ICellOrganizationScope.MaxOrganizationsPerCheck, nameof(organizationIds));
        var distinct = organizationIds.Distinct().ToArray();
        if (distinct.Length == 0)
        {
            return true;
        }

        await using var command = dataSource.CreateCommand(Sql);
        command.Parameters.Add(new NpgsqlParameter<Guid[]> { TypedValue = distinct });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = cellId });
        return await command.ExecuteScalarAsync(cancellationToken) is long count && count == distinct.Length;
    }
}
