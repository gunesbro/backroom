using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Backroom.BuildingBlocks.Application.Organizations;
using Backroom.BuildingBlocks.Application.Persistence;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.Modules.Permissions.Contracts;
using Backroom.Modules.Permissions.Infrastructure;
using Backroom.Testing.Postgres;

namespace Backroom.Modules.Permissions.IntegrationTests;

/// <summary>The contract's permission table and role grants, under row-level security with the application role.</summary>
[Trait("Category", "Privacy")]
public sealed class PermissionCheckerTests(PostgresDatabase database) : IAsyncLifetime
{
    private static readonly SemaphoreSlim s_migrationLock = new(1, 1);
    private static bool s_migrated;

    private ServiceProvider _services = null!;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await MigrateAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDatabaseSession().Configure(options => options.ConnectionString = database.ApplicationConnectionString());
        services.AddPermissions();
        _services = services.BuildServiceProvider();
    }

    public async ValueTask DisposeAsync() => await _services.DisposeAsync();

    [Theory]
    [InlineData(SystemRoles.Owner, SystemRoles.ChannelsCreatePublic, true)]
    [InlineData(SystemRoles.Owner, SystemRoles.MembersInviteByEmail, true)]
    [InlineData(SystemRoles.Owner, SystemRoles.MembersChooseRoleOnInvite, true)]
    [InlineData(SystemRoles.Owner, SystemRoles.MembersManageInvitations, true)]
    [InlineData(SystemRoles.Admin, SystemRoles.ChannelsCreatePublic, true)]
    [InlineData(SystemRoles.Admin, SystemRoles.MembersInviteByEmail, true)]
    [InlineData(SystemRoles.Admin, SystemRoles.MembersChooseRoleOnInvite, true)]
    [InlineData(SystemRoles.Admin, SystemRoles.MembersManageInvitations, true)]
    [InlineData(SystemRoles.Member, SystemRoles.ChannelsCreatePublic, true)]
    [InlineData(SystemRoles.Member, SystemRoles.MembersInviteByEmail, true)]
    [InlineData(SystemRoles.Member, SystemRoles.MembersChooseRoleOnInvite, false)]
    [InlineData(SystemRoles.Member, SystemRoles.MembersManageInvitations, false)]
    [InlineData(SystemRoles.Owner, SystemRoles.FilesUpload, true)]
    [InlineData(SystemRoles.Owner, SystemRoles.FilesRemoveOthersPublic, true)]
    [InlineData(SystemRoles.Admin, SystemRoles.FilesUpload, true)]
    [InlineData(SystemRoles.Admin, SystemRoles.FilesRemoveOthersPublic, true)]
    [InlineData(SystemRoles.Member, SystemRoles.FilesUpload, true)]
    [InlineData(SystemRoles.Member, SystemRoles.FilesRemoveOthersPublic, false)]
    public async Task HasPermission_ForEachRole_FollowsTheContractTableAsync(string role, string permission, bool expected)
    {
        var (organizationId, accountId) = await OrganizationWithAccountAsync(role);

        var has = await InOrganizationAsync(organizationId, checker => checker.HasPermissionAsync(accountId, permission, CancellationToken));

        has.Should().Be(expected);
    }

    [Fact]
    public async Task HasPermission_WithoutARole_IsFalseAsync()
    {
        var (organizationId, _) = await OrganizationWithAccountAsync(SystemRoles.Owner);

        var has = await InOrganizationAsync(organizationId, checker => checker.HasPermissionAsync(Guid.CreateVersion7(), SystemRoles.ChannelsCreatePublic, CancellationToken));

        has.Should().BeFalse();
    }

    [Fact]
    public async Task HasPermission_InAnotherOrganization_IsFalseEvenForAnOwnerAsync()
    {
        var (_, owner) = await OrganizationWithAccountAsync(SystemRoles.Owner);
        var (otherOrganization, _) = await OrganizationWithAccountAsync(SystemRoles.Owner);

        var has = await InOrganizationAsync(otherOrganization, checker => checker.HasPermissionAsync(owner, SystemRoles.MembersManageInvitations, CancellationToken));

        has.Should().BeFalse("a role in one organization grants nothing in another");
    }

    [Fact]
    public async Task HasPermission_ForAnOwnerWithoutThePermissionRow_IsStillTrueAsync()
    {
        // A permission added after an organization's roles were created has no row on its Owner role.
        var (organizationId, owner) = await OrganizationWithAccountAsync(SystemRoles.Owner);
        await using (var connection = new NpgsqlConnection(database.SuperuserConnectionString))
        {
            await connection.OpenAsync(CancellationToken);
            await using var delete = new NpgsqlCommand("DELETE FROM permissions.role_permissions WHERE org_id = $1", connection);
            delete.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organizationId });
            await delete.ExecuteNonQueryAsync(CancellationToken);
        }

        var has = await InOrganizationAsync(organizationId, checker => checker.HasPermissionAsync(owner, SystemRoles.MembersManageInvitations, CancellationToken));

        has.Should().BeTrue();
    }

    [Fact]
    public async Task HasPermission_WithAnUnknownKey_ThrowsInsteadOfDenyingAsync()
    {
        var (organizationId, owner) = await OrganizationWithAccountAsync(SystemRoles.Owner);

        var check = () => InOrganizationAsync(organizationId, checker => checker.HasPermissionAsync(owner, "members.invite-by-mail", CancellationToken));

        await check.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task HasPermission_AfterARoleIsAdded_AppliesOnTheNextRequestAsync()
    {
        var (organizationId, accountId) = await OrganizationWithAccountAsync(SystemRoles.Member);
        (await InOrganizationAsync(organizationId, checker => checker.HasPermissionAsync(accountId, SystemRoles.MembersManageInvitations, CancellationToken))).Should().BeFalse();

        await InOrganizationAsync(organizationId, async (provisioning, _) =>
        {
            await provisioning.AssignRoleAsync(organizationId, accountId, SystemRoles.Admin, CancellationToken);
            return true;
        });

        (await InOrganizationAsync(organizationId, checker => checker.HasPermissionAsync(accountId, SystemRoles.MembersManageInvitations, CancellationToken))).Should().BeTrue();
    }

    [Theory]
    [InlineData(SystemRoles.Owner, SystemRoles.Owner, false)]
    [InlineData(SystemRoles.Owner, SystemRoles.Admin, true)]
    [InlineData(SystemRoles.Owner, SystemRoles.Member, true)]
    [InlineData(SystemRoles.Admin, SystemRoles.Owner, false)]
    [InlineData(SystemRoles.Admin, SystemRoles.Admin, true)]
    [InlineData(SystemRoles.Admin, SystemRoles.Member, true)]
    [InlineData(SystemRoles.Member, SystemRoles.Owner, false)]
    [InlineData(SystemRoles.Member, SystemRoles.Admin, false)]
    [InlineData(SystemRoles.Member, SystemRoles.Member, true)]
    [InlineData(SystemRoles.Admin, "no-such-role", false)]
    [InlineData(SystemRoles.Owner, "OWNER", false)]
    public async Task CanGrantRole_ForEachRole_NeverGoesAboveTheGrantersOwnAsync(string granterRole, string role, bool expected)
    {
        var (organizationId, granter) = await OrganizationWithAccountAsync(granterRole);

        var can = await InOrganizationAsync(organizationId, checker => checker.CanGrantRoleAsync(granter, role, CancellationToken));

        can.Should().Be(expected);
    }

    [Fact]
    public async Task CanGrantRole_InAnotherOrganization_IsFalseEvenForAnOwnerAsync()
    {
        var (_, owner) = await OrganizationWithAccountAsync(SystemRoles.Owner);
        var (otherOrganization, _) = await OrganizationWithAccountAsync(SystemRoles.Owner);

        var can = await InOrganizationAsync(otherOrganization, checker => checker.CanGrantRoleAsync(owner, SystemRoles.Member, CancellationToken));

        can.Should().BeFalse();
    }

    [Fact]
    public async Task Checker_ReadsRolesOnly_SoItDoesNotProveAMembershipAsync()
    {
        // Pins the interface's warning: this account was never a member (Permissions has no member table), yet its
        // role answers yes. A caller outside InOrganization() must check the membership itself.
        var (organizationId, accountWithARole) = await OrganizationWithAccountAsync(SystemRoles.Owner);

        var has = await InOrganizationAsync(organizationId, checker => checker.HasPermissionAsync(accountWithARole, SystemRoles.MembersManageInvitations, CancellationToken));
        var can = await InOrganizationAsync(organizationId, checker => checker.CanGrantRoleAsync(accountWithARole, SystemRoles.Member, CancellationToken));

        has.Should().BeTrue();
        can.Should().BeTrue();
    }

    [Fact]
    public async Task RemoveMember_RemovesEveryRoleOfTheAccountInThatOrganizationOnlyAsync()
    {
        var (organizationId, accountId) = await OrganizationWithAccountAsync(SystemRoles.Owner);
        var (otherOrganization, _) = await OrganizationWithAccountAsync(SystemRoles.Owner);
        var colleague = Guid.CreateVersion7();
        await InOrganizationAsync(organizationId, async (provisioning, _) =>
        {
            await provisioning.AssignRoleAsync(organizationId, accountId, SystemRoles.Admin, CancellationToken);
            await provisioning.AssignRoleAsync(organizationId, colleague, SystemRoles.Admin, CancellationToken);
            return true;
        });
        await InOrganizationAsync(otherOrganization, async (provisioning, _) =>
        {
            await provisioning.AssignRoleAsync(otherOrganization, accountId, SystemRoles.Member, CancellationToken);
            return true;
        });

        await InOrganizationAsync(organizationId, async (provisioning, _) =>
        {
            await provisioning.RemoveMemberAsync(organizationId, accountId, CancellationToken);
            return true;
        });

        (await InOrganizationAsync(organizationId, checker => checker.HasPermissionAsync(accountId, SystemRoles.ChannelsCreatePublic, CancellationToken)))
            .Should().BeFalse("neither Owner nor Admin is left");
        (await InOrganizationAsync(organizationId, checker => checker.HasPermissionAsync(colleague, SystemRoles.MembersManageInvitations, CancellationToken)))
            .Should().BeTrue("another account keeps its role");
        (await InOrganizationAsync(otherOrganization, checker => checker.HasPermissionAsync(accountId, SystemRoles.ChannelsCreatePublic, CancellationToken)))
            .Should().BeTrue("the account's role in another organization stays");
    }

    [Fact]
    public async Task CanGrantRole_WithoutARole_IsFalseAsync()
    {
        var (organizationId, _) = await OrganizationWithAccountAsync(SystemRoles.Owner);

        var can = await InOrganizationAsync(organizationId, checker => checker.CanGrantRoleAsync(Guid.CreateVersion7(), SystemRoles.Member, CancellationToken));

        can.Should().BeFalse();
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100", Justification = "The SQL is the migration's own constant text.")]
    public async Task AddFilePermissionsMigration_ForAnExistingOrganization_GrantsTheFileKeysAndKeepsRowSecurityForcedAsync()
    {
        // An organization from before Phase 2: its roles lack the file permissions.
        var (organizationId, admin) = await OrganizationWithAccountAsync(SystemRoles.Admin);
        var member = Guid.CreateVersion7();
        await InOrganizationAsync(organizationId, async (provisioning, _) =>
        {
            await provisioning.AssignRoleAsync(organizationId, member, SystemRoles.Member, CancellationToken);
            return true;
        });
        await using (var superuser = new NpgsqlConnection(database.SuperuserConnectionString))
        {
            await superuser.OpenAsync(CancellationToken);
            await using var delete = new NpgsqlCommand("DELETE FROM permissions.role_permissions WHERE org_id = $1 AND permission LIKE 'files.%'", superuser);
            delete.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = organizationId });
            await delete.ExecuteNonQueryAsync(CancellationToken);
        }

        (await InOrganizationAsync(organizationId, checker => checker.HasPermissionAsync(member, SystemRoles.FilesUpload, CancellationToken))).Should().BeFalse();

        // The migration's statement, as the migrator (the tables' owner) runs it, twice: it is idempotent.
        await using (var migrator = new NpgsqlConnection(database.MigratorConnectionString))
        {
            await migrator.OpenAsync(CancellationToken);
            for (var run = 0; run < 2; run++)
            {
                await using var transaction = await migrator.BeginTransactionAsync(CancellationToken);
                await using var backfill = new NpgsqlCommand(OrganizationDataMigrationExtensions.OrganizationDataSqlText(PermissionsDbContext.Schema, Infrastructure.Migrations.AddFilePermissions.Tables, Infrastructure.Migrations.AddFilePermissions.InsertSql), migrator, transaction);
                await backfill.ExecuteNonQueryAsync(CancellationToken);
                await transaction.CommitAsync(CancellationToken);
            }

            await using var forced = new NpgsqlCommand(
                "SELECT bool_and(relforcerowsecurity) FROM pg_class WHERE oid IN ('permissions.roles'::regclass, 'permissions.role_permissions'::regclass)", migrator);
            (await forced.ExecuteScalarAsync(CancellationToken)).Should().Be(true);
        }

        (await InOrganizationAsync(organizationId, checker => checker.HasPermissionAsync(member, SystemRoles.FilesUpload, CancellationToken))).Should().BeTrue();
        (await InOrganizationAsync(organizationId, checker => checker.HasPermissionAsync(member, SystemRoles.FilesRemoveOthersPublic, CancellationToken))).Should().BeFalse();
        (await InOrganizationAsync(organizationId, checker => checker.HasPermissionAsync(admin, SystemRoles.FilesRemoveOthersPublic, CancellationToken))).Should().BeTrue();
    }

    private async Task<(Guid OrganizationId, Guid AccountId)> OrganizationWithAccountAsync(string role)
    {
        var organizationId = Guid.CreateVersion7();
        var accountId = Guid.CreateVersion7();
        await InOrganizationAsync(organizationId, async (provisioning, _) =>
        {
            await provisioning.InitializeOrganizationAsync(organizationId, CancellationToken);
            await provisioning.AssignRoleAsync(organizationId, accountId, role, CancellationToken);
            return true;
        });
        return (organizationId, accountId);
    }

    private Task<T> InOrganizationAsync<T>(Guid organizationId, Func<IPermissionChecker, Task<T>> action) =>
        InOrganizationAsync(organizationId, (_, checker) => action(checker));

    /// <summary>One request's scope and unit of work inside the organization.</summary>
    private async Task<T> InOrganizationAsync<T>(Guid organizationId, Func<IPermissionsProvisioning, IPermissionChecker, Task<T>> action)
    {
        await using var scope = _services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<OrganizationContext>().Enter(organizationId);
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await unitOfWork.BeginAsync(CancellationToken);
        var result = await action(
            scope.ServiceProvider.GetRequiredService<IPermissionsProvisioning>(),
            scope.ServiceProvider.GetRequiredService<IPermissionChecker>());
        await unitOfWork.CommitAsync(CancellationToken);
        return result;
    }

    private async Task MigrateAsync()
    {
        await s_migrationLock.WaitAsync(CancellationToken);
        try
        {
            if (s_migrated)
            {
                return;
            }

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddPermissionsDatabase();
            services.AddDatabaseMigrations(allowBootstrap: false).Configure(options => options.MigratorConnectionString = database.MigratorConnectionString);
            await using var provider = services.BuildServiceProvider();
            await provider.GetRequiredService<DatabaseMigrator>().MigrateAsync(CancellationToken);
            s_migrated = true;
        }
        finally
        {
            s_migrationLock.Release();
        }
    }
}
