using AwesomeAssertions;
using Backroom.BuildingBlocks.Infrastructure.Persistence;

namespace Backroom.BuildingBlocks.UnitTests.Persistence;

public sealed class OrganizationRowLevelSecurityTests
{
    [Fact]
    public void EnableSql_WhenCalled_EnablesForcesAndAddsThePolicy()
    {
        var sql = OrganizationRowLevelSecurity.EnableSql("messaging", "channels");

        sql.Should().Contain("ALTER TABLE \"messaging\".\"channels\" ENABLE ROW LEVEL SECURITY;")
            .And.Contain("ALTER TABLE \"messaging\".\"channels\" FORCE ROW LEVEL SECURITY;")
            .And.Contain("CREATE POLICY org_isolation ON \"messaging\".\"channels\" FOR ALL")
            .And.Contain("WITH CHECK (org_id = NULLIF(current_setting('app.org_id', true), '')::uuid)");
    }

    [Theory]
    [InlineData("Channels")]
    [InlineData("channels\"; DROP TABLE x; --")]
    [InlineData("channels.messages")]
    [InlineData("kanallar_ı")]
    [InlineData("1channels")]
    [InlineData("")]
    [InlineData("a234567890123456789012345678901234567890123456789012345678901234")]
    public void EnableSql_WhenIdentifierIsNotSnakeCase_Throws(string table)
    {
        var create = () => OrganizationRowLevelSecurity.EnableSql("messaging", table);

        create.Should().Throw<ArgumentException>();
    }
}
