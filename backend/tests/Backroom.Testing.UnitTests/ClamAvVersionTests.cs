using AwesomeAssertions;
using Backroom.Testing.ClamAv;

namespace Backroom.Testing.UnitTests;

/// <summary>The scan tests must run the ClamAV image that development runs.</summary>
public sealed class ClamAvVersionTests
{
    [Fact]
    public void AppHostImage_ComparedWithTheTestContainer_IsTheSameVersion()
    {
        var appHost = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "apphost", "DevelopmentServices.cs"));

        appHost.Should().Contain($"ClamAvImageTag = \"{ClamAvServer.Version}\"");
    }

    [Fact]
    public void AppHostImage_Pinned_IsNeverLatest()
    {
        ClamAvServer.Version.Should().MatchRegex(@"^\d+\.\d+\.\d+$");
    }
}
