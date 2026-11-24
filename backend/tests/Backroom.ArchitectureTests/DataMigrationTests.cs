using AwesomeAssertions;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Backroom.ArchitectureTests;

public sealed class DataMigrationTests
{
    [Fact]
    public void Migrations_WithDataStatements_UseOrganizationDataSql()
    {
        var violations = DataMigrationRules.FindViolations(SolutionAssemblies.All);

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Migrations_AreFound()
    {
        SolutionAssemblies.All
            .SelectMany(assembly => assembly.GetTypes())
            .Count(type => typeof(Microsoft.EntityFrameworkCore.Migrations.Migration).IsAssignableFrom(type))
            .Should().BeGreaterThan(10, "the rule must see the modules' migrations");
    }

    [Theory]
    [InlineData("UPDATE organization.invitations SET email = NULL")]
    [InlineData("update organization.invitations i set email = null")]
    [InlineData("UPDATE \"email\".\"deliveries\" AS d SET status = 'pending'")]
    [InlineData("INSERT INTO messaging.members (id) SELECT id FROM messaging.channels")]
    [InlineData("DELETE FROM organization.invitations WHERE email IS NULL")]
    [InlineData("WITH due AS (SELECT id FROM t) UPDATE s.t SET a = 1 FROM due")]
    [InlineData("MERGE INTO s.t USING s.u ON true WHEN MATCHED THEN DELETE")]
    public void FindViolations_WhenPlainSqlChangesData_ReturnsViolation(string sql)
    {
        DataMigrationRules.FindViolations("m", [new SqlOperation { Sql = sql }]).Should().ContainSingle();
    }

    [Theory]
    [InlineData("GRANT UPDATE (status, email) ON organization.invitations TO backroom_maintenance")]
    [InlineData("REVOKE INSERT, UPDATE, DELETE ON s.t FROM backroom_app")]
    [InlineData("CREATE TABLE s.t (id uuid, org_id uuid REFERENCES s.o (id) ON DELETE CASCADE ON UPDATE SET NULL)")]
    [InlineData("CREATE POLICY p ON s.t FOR UPDATE USING (true)")]
    [InlineData("-- UPDATE s.t SET a = 1 in a comment\nCREATE INDEX i ON s.t (a)")]
    public void FindViolations_WhenSqlChangesNoData_ReturnsNothing(string sql)
    {
        DataMigrationRules.FindViolations("m", [new SqlOperation { Sql = sql }]).Should().BeEmpty();
    }

    [Fact]
    public void FindViolations_WhenDataStatementUsesTheHelper_ReturnsNothing()
    {
        var sql = OrganizationDataMigrationExtensions.OrganizationDataSqlText("s", ["t"], "UPDATE s.t SET a = 1");

        DataMigrationRules.FindViolations("m", [new SqlOperation { Sql = sql }]).Should().BeEmpty();
        sql.Should().Contain("ALTER TABLE \"s\".\"t\" NO FORCE ROW LEVEL SECURITY;").And.EndWith("ALTER TABLE \"s\".\"t\" FORCE ROW LEVEL SECURITY;\n");
    }

    [Fact]
    public void FindViolations_WhenMigrationUsesDataOperations_ReturnsViolation()
    {
        DataMigrationRules.FindViolations("m", [new InsertDataOperation { Table = "t" }]).Should().ContainSingle();
    }
}
