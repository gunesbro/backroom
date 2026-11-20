using Npgsql;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.Modules.Organization.Application.Provisioning;
using Backroom.Modules.Organization.Contracts;

namespace Backroom.Modules.Organization.Infrastructure;

/// <summary>Organization's tables with hand-written SQL in the scope's unit of work (row-level security applies).</summary>
internal sealed class OrganizationStore(DatabaseSession session) : IOrganizationStore
{
    private const string AddSql = """
        INSERT INTO organization.organizations (id, org_id, name, owner_account_id, version, created_at)
        VALUES ($1, $1, $2, $3, 1, $4)
        ON CONFLICT (id) DO NOTHING
        """;

    private const string OwnerSql = "SELECT owner_account_id FROM organization.organizations WHERE id = $1";

    private const string AddOwnerSql = """
        INSERT INTO organization.members (org_id, account_id, display_name, email, locale, status, version, joined_at)
        VALUES ($1, $2, $3, $4, $5, 'active', 1, $6)
        """;

    public async Task<Guid?> AddOrGetOwnerAsync(NewOrganization organization, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organization);
        await using (var add = new NpgsqlCommand(AddSql, session.Connection, session.Transaction))
        {
            add.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organization.Id });
            add.Parameters.Add(new NpgsqlParameter<string> { TypedValue = organization.Name });
            add.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organization.OwnerAccountId });
            add.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = now });
            if (await add.ExecuteNonQueryAsync(cancellationToken) == 1)
            {
                return null;
            }
        }

        await using var owner = new NpgsqlCommand(OwnerSql, session.Connection, session.Transaction);
        owner.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organization.Id });
        return (Guid)(await owner.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task AddOwnerAsync(NewOrganization organization, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organization);
        await using var command = new NpgsqlCommand(AddOwnerSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organization.Id });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organization.OwnerAccountId });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = organization.OwnerDisplayName });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = organization.OwnerEmail });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = organization.OwnerLocale });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = now });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
