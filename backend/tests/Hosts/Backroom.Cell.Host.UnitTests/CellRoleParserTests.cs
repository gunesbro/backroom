using AwesomeAssertions;

namespace Backroom.Cell.Host.UnitTests;

public sealed class CellRoleParserTests
{
    [Theory]
    [InlineData("app", CellRole.App)]
    [InlineData("APP", CellRole.App)]
    [InlineData("worker", CellRole.Worker)]
    [InlineData("WORKER", CellRole.Worker)]
    [InlineData("Worker", CellRole.Worker)]
    [InlineData("migrate", CellRole.Migrate)]
    public void Parse_WhenValueIsKnownRole_ReturnsRole(string value, CellRole expected)
    {
        CellRoleParser.Parse(value).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("both")]
    public void Parse_WhenValueIsMissingOrUnknown_Throws(string? value)
    {
        var parse = () => CellRoleParser.Parse(value);

        parse.Should().Throw<InvalidOperationException>().WithMessage("*Cell:Role*");
    }
}
