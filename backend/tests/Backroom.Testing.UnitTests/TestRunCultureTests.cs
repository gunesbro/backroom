using System.Globalization;
using AwesomeAssertions;

namespace Backroom.Testing.UnitTests;

/// <summary>
/// Guards the CI run that is meant to execute the whole suite under tr-TR:
/// if the environment does not produce the expected culture, the run fails instead of passing silently.
/// </summary>
public sealed class TestRunCultureTests
{
    public const string ExpectedCultureVariable = "BACKROOM_EXPECTED_CULTURE";

    [Fact]
    public void CurrentCulture_WhenExpectedCultureIsSet_MatchesIt()
    {
        var expected = Environment.GetEnvironmentVariable(ExpectedCultureVariable);
        Assert.SkipWhen(string.IsNullOrEmpty(expected), $"{ExpectedCultureVariable} is not set.");

        CultureInfo.CurrentCulture.Name.Should().Be(expected);
    }
}
