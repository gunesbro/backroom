using AwesomeAssertions;
using Backroom.BuildingBlocks.Presentation.Authentication;

namespace Backroom.BuildingBlocks.UnitTests.Presentation;

public sealed class AudiencesTests
{
    [Theory]
    [InlineData("a", "cell-a")]
    [InlineData("eu-west-1", "cell-eu-west-1")]
    [InlineData("0f8a3c2e-7d1b-4c5e-9a6f-2b3c4d5e6f70", "cell-0f8a3c2e-7d1b-4c5e-9a6f-2b3c4d5e6f70")]
    public void Cell_WhenIdIsValid_ReturnsItsAudience(string cellId, string audience)
    {
        Audiences.Cell(cellId).Should().Be(audience);
    }

    [Theory]
    [InlineData("")]
    [InlineData("A")]
    [InlineData("-a")]
    [InlineData("a b")]
    [InlineData("a,control-plane")]
    [InlineData("ı")]
    public void Cell_WhenIdIsInvalid_Throws(string cellId)
    {
        var audience = () => Audiences.Cell(cellId);

        audience.Should().Throw<ArgumentException>();
    }
}
