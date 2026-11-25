using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Backroom.BuildingBlocks.Application.Persistence;
using Backroom.ControlPlane.Contracts.V1;
using Backroom.Modules.CellManagement.Contracts;
using Backroom.Modules.ControlPlaneConnector.Infrastructure.Link;
using Backroom.Modules.Organization.Infrastructure;
using Backroom.Modules.Permissions.Contracts;
using Backroom.Modules.Permissions.Infrastructure;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;

namespace Backroom.Modules.CellManagement.IntegrationTests;

/// <summary>
/// A new organization reaches its cell (docs/contracts/phase-1.md, flow 2, step 3): the control plane sends
/// <c>OrganizationProvisioned</c>, the cell creates the organization, its owner and the system roles in one
/// unit of work under the organization's row-level security, and acknowledges.
/// </summary>
[Collection(LinkWorkerCollection.Name)]
[Trait("Category", "Privacy")]
public sealed class CellOrganizationProvisioningTests(PostgresDatabase database, KeycloakRealm keycloak) : IAsyncLifetime
{
    private WebApplication _controlPlane = null!;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _controlPlane = await ControlPlaneUnderTest.StartAsync(database, keycloak);

    public async ValueTask DisposeAsync() => await _controlPlane.DisposeAsync();

    [Fact]
    public async Task OrganizationProvisioned_CreatesTheOrganizationItsOwnerAndTheSystemRolesAsync()
    {
        await using var cell = await StartCellAsync();
        var organization = Guid.CreateVersion7();
        var owner = Guid.CreateVersion7();

        var messageId = await EnqueueAsync(cell.CellId, organization, owner);

        await WaitUntilAsync(async () => await StateAsync(messageId) == "acked");
        (await CountAsync("SELECT count(*) FROM organization.organizations WHERE id = $1 AND owner_account_id = $2", organization, owner)).Should().Be(1);
        (await CountAsync("SELECT count(*) FROM organization.members WHERE org_id = $1 AND account_id = $2 AND status = 'active'", organization, owner)).Should().Be(1);
        (await CountAsync("SELECT count(*) FROM permissions.roles WHERE org_id = $1 AND is_system", organization)).Should().Be(3);
        (await CountAsync(
            "SELECT count(*) FROM permissions.member_roles m JOIN permissions.roles r ON r.id = m.role_id WHERE m.org_id = $1 AND m.account_id = $2 AND r.key = 'owner'",
            organization, owner)).Should().Be(1);

        // The contracts' permission tables (Phase 1, and Phase 2 flow 6 for files); Owner has every permission.
        (await PermissionsAsync(organization, "owner")).Should().BeEquivalentTo(SystemRoles.AllPermissions);
        (await PermissionsAsync(organization, "admin")).Should().BeEquivalentTo(
            ["channels.create-public", "members.invite-by-email", "members.choose-role-on-invite", "members.manage-invitations", "files.upload", "files.remove-others-public",
             "channels.create-private", "channels.manage-public"]);
        (await PermissionsAsync(organization, "member")).Should().BeEquivalentTo(["channels.create-public", "members.invite-by-email", "files.upload", "channels.create-private"]);
    }

    [Fact]
    public async Task OrganizationProvisioned_SentAgain_ChangesNothingAsync()
    {
        await using var cell = await StartCellAsync();
        var organization = Guid.CreateVersion7();
        var owner = Guid.CreateVersion7();

        var first = await EnqueueAsync(cell.CellId, organization, owner);
        var again = await EnqueueAsync(cell.CellId, organization, owner);

        await WaitUntilAsync(async () => await StateAsync(first) == "acked" && await StateAsync(again) == "acked");
        (await CountAsync("SELECT count(*) FROM organization.members WHERE org_id = $1", organization)).Should().Be(1);
        (await CountAsync("SELECT count(*) FROM permissions.roles WHERE org_id = $1", organization)).Should().Be(3);
        (await CountAsync("SELECT count(*) FROM permissions.member_roles WHERE org_id = $1", organization)).Should().Be(1);
        (await CountAsync("SELECT count(*) FROM permissions.role_permissions WHERE org_id = $1", organization)).Should().Be(SystemRoles.Permissions.Values.Sum(permissions => permissions.Count));
    }

    [Fact]
    public async Task OrganizationProvisioned_ForAnExistingOrganizationOfAnotherOwner_IsRejectedAsync()
    {
        await using var cell = await StartCellAsync();
        var organization = Guid.CreateVersion7();
        var first = await EnqueueAsync(cell.CellId, organization, Guid.CreateVersion7());
        await WaitUntilAsync(async () => await StateAsync(first) == "acked");

        var other = await EnqueueAsync(cell.CellId, organization, Guid.CreateVersion7());

        await WaitUntilAsync(async () => await StateAsync(other) == "organization.exists");
    }

    [Fact]
    public async Task OrganizationProvisioned_WithAnInvalidOwner_IsRejectedAsync()
    {
        await using var cell = await StartCellAsync();

        var messageId = await EnqueueAsync(cell.CellId, Guid.CreateVersion7(), owner: null);

        await WaitUntilAsync(async () => await StateAsync(messageId) == "organization.provisioning_invalid");
    }

    public static TheoryData<string> InvalidValues => new() { "empty owner", "long name", "email without @", "short locale", "NUL in name" };

    [Theory]
    [MemberData(nameof(InvalidValues))]
    public async Task OrganizationProvisioned_WithValuesTheColumnsCannotHold_IsRejectedAsync(string kind)
    {
        await using var cell = await StartCellAsync();
        var message = Provisioned(Guid.CreateVersion7(), Guid.CreateVersion7());
        switch (kind)
        {
            case "empty owner": message.OwnerAccountId = Guid.Empty.ToString("D"); break;
            case "long name": message.Name = new string('a', 401); break;
            case "email without @": message.OwnerEmail = "alice.example.test"; break;
            case "short locale": message.OwnerLocale = "e"; break;
            default: message.Name = "Ac\0me"; break;
        }

        var messageId = await EnqueueAsync(cell.CellId, message);

        await WaitUntilAsync(async () => await StateAsync(messageId) == "organization.provisioning_invalid");
    }

    [Fact]
    public async Task ProvisionedOrganization_IsInvisibleFromAnotherOrganizationAsync()
    {
        await using var cell = await StartCellAsync();
        var organization = Guid.CreateVersion7();
        var messageId = await EnqueueAsync(cell.CellId, organization, Guid.CreateVersion7());
        await WaitUntilAsync(async () => await StateAsync(messageId) == "acked");

        (await CountAsApplicationAsync(organization, "SELECT count(*) FROM organization.members")).Should().Be(1);
        var another = Guid.CreateVersion7();
        foreach (var table in new[] { "organization.organizations", "organization.members", "permissions.roles", "permissions.role_permissions", "permissions.member_roles" })
        {
            (await CountAsApplicationAsync(another, $"SELECT count(*) FROM {table} WHERE org_id = $1", organization)).Should().Be(0, table);
        }

        // Writes too: from another organization's context no row of this organization can be added.
        var write = () => CountAsApplicationAsync(
            another,
            "WITH added AS (INSERT INTO organization.members (org_id, account_id, display_name, email, locale, status, version, joined_at) VALUES ($1, $1, 'x', 'x@x', 'en', 'active', 1, now()) RETURNING 1) SELECT count(*) FROM added",
            organization);
        var refused = (await write.Should().ThrowAsync<PostgresException>()).Which;
        refused.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        refused.MessageText.Should().Contain("row-level security", "a missing grant gives the same code");
    }

    private async Task<TestCell> StartCellAsync()
    {
        var cell = await TestCell.RegisterAsync(_controlPlane, keycloak, CancellationToken, database, configureServices: services =>
        {
            services.AddPermissions();
            services.AddOrganization();
            services.TryAddEnumerable(ServiceDescriptor.Scoped<IDownstreamHandler<OrganizationProvisioned>, OrganizationProvisionedHandler>());
        });
        await cell.Link.StartAsync(CancellationToken);
        await WaitUntilAsync(() => Task.FromResult(cell.Link.IsConnected));
        return cell;
    }

    private Task<Guid> EnqueueAsync(string cellId, Guid organization, Guid? owner)
    {
        var message = Provisioned(organization, owner ?? Guid.Empty);
        if (owner is null)
        {
            message.OwnerAccountId = "not-an-account";
        }

        return EnqueueAsync(cellId, message);
    }

    private static OrganizationProvisioned Provisioned(Guid organization, Guid owner) => new()
    {
        OrganizationId = organization.ToString("D"),
        Name = "Acme",
        OwnerAccountId = owner.ToString("D"),
        OwnerDisplayName = "Alice Test",
        OwnerEmail = "alice@example.test",
        OwnerLocale = "en-US",
    };

    private async Task<Guid> EnqueueAsync(string cellId, OrganizationProvisioned message)
    {
        await using var scope = _controlPlane.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await unitOfWork.BeginAsync(CancellationToken);
        var messageId = await scope.ServiceProvider.GetRequiredService<ICellLinkOutbox>().EnqueueAsync(
            cellId, new ConnectResponse { OrganizationProvisioned = message }, CancellationToken);
        await unitOfWork.CommitAsync(CancellationToken);
        return messageId;
    }

    private async Task<List<string>> PermissionsAsync(Guid organization, string role)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT p.permission FROM permissions.role_permissions p JOIN permissions.roles r ON r.id = p.role_id WHERE r.org_id = $1 AND r.key = $2",
            connection);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organization });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = role });
        await using var reader = await command.ExecuteReaderAsync(CancellationToken);
        var permissions = new List<string>();
        while (await reader.ReadAsync(CancellationToken))
        {
            permissions.Add(reader.GetString(0));
        }

        return permissions;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        for (var attempt = 0; attempt < 150 && !await condition(); attempt++)
        {
            await Task.Delay(100, CancellationToken);
        }

        (await condition()).Should().BeTrue();
    }

    private async Task<string?> StateAsync(Guid messageId)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT CASE WHEN acked_at IS NOT NULL THEN 'acked' WHEN rejected_code IS NOT NULL THEN rejected_code ELSE 'pending' END FROM cell_management.outbound_messages WHERE message_id = $1",
            connection);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = messageId });
        return await command.ExecuteScalarAsync(CancellationToken) as string;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100", Justification = "Test SQL is constant; values are parameters.")]
    private async Task<long> CountAsync(string sql, params Guid[] values)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = value });
        }

        return (long)(await command.ExecuteScalarAsync(CancellationToken))!;
    }

    // As the application role in an organization, the way every request and message runs.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100", Justification = "Test SQL is constant; values are parameters.")]
    private async Task<long> CountAsApplicationAsync(Guid organizationContext, string sql, params Guid[] values)
    {
        await using var connection = new NpgsqlConnection(database.ApplicationConnectionString());
        await connection.OpenAsync(CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(CancellationToken);
        await using (var context = new NpgsqlCommand("SELECT set_config('app.org_id', $1, true)", connection, transaction))
        {
            context.Parameters.Add(new NpgsqlParameter<string> { TypedValue = organizationContext.ToString("D") });
            await context.ExecuteNonQueryAsync(CancellationToken);
        }

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = value });
        }

        return (long)(await command.ExecuteScalarAsync(CancellationToken))!;
    }
}
