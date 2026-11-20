using AwesomeAssertions;
using Backroom.BuildingBlocks.Infrastructure.Maintenance;

namespace Backroom.BuildingBlocks.UnitTests.Maintenance;

public sealed class MaintenanceGrantsTests
{
    [Fact]
    public void GrantColumnsSql_WithSelectAndUpdateColumns_GrantsOnlyThoseColumns()
    {
        var sql = MaintenanceGrants.GrantColumnsSql("organization", "invitations", ["id", "status"], ["status"]);

        sql.Should().Be(
            "GRANT SELECT (\"id\", \"status\") ON \"organization\".\"invitations\" TO backroom_maintenance;\n"
            + "GRANT UPDATE (\"status\") ON \"organization\".\"invitations\" TO backroom_maintenance;");
    }

    [Fact]
    public void GrantColumnsSql_WithOnlySelectColumns_OmitsTheUpdateGrant()
    {
        MaintenanceGrants.GrantColumnsSql("organization", "invitations", ["id"], []).Should().NotContain("UPDATE");
    }

    [Fact]
    public void GrantColumnsSql_WithoutColumns_Throws()
    {
        var grant = () => MaintenanceGrants.GrantColumnsSql("organization", "invitations", [], []);

        grant.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("Invitations", "id")]
    [InlineData("invitations", "id\"; DROP TABLE x; --")]
    [InlineData("invitations", "Email")]
    [InlineData("invitations", "")]
    public void GrantColumnsSql_WithAnInvalidIdentifier_Throws(string table, string column)
    {
        var grant = () => MaintenanceGrants.GrantColumnsSql("organization", table, [column], []);

        grant.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void GrantColumnsSql_WithTooManyColumns_Throws()
    {
        var columns = Enumerable.Range(0, 33).Select(index => $"c{index}").ToList();

        var grant = () => MaintenanceGrants.GrantColumnsSql("organization", "invitations", columns, []);

        grant.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void GrantDeleteSql_GrantsDeleteOnTheQuotedTable()
    {
        MaintenanceGrants.GrantDeleteSql("email", "deliveries").Should().Be("GRANT DELETE ON \"email\".\"deliveries\" TO backroom_maintenance;");
    }

    [Fact]
    public void RevokeAllSql_RevokesEveryPrivilegeOfTheMaintenanceRole()
    {
        MaintenanceGrants.RevokeAllSql("email", "deliveries").Should().Be("REVOKE ALL ON \"email\".\"deliveries\" FROM backroom_maintenance;");
    }
}
