using AwesomeAssertions;
using Backroom.Testing.SeaweedFs;

namespace Backroom.Testing.UnitTests;

/// <summary>The file tests must run the SeaweedFS that development runs, so uploads and signed URLs behave the same.</summary>
public sealed class SeaweedFsVersionTests
{
    [Fact]
    public void AppHostImage_ComparedWithTheTestContainer_IsTheSameVersion()
    {
        var appHost = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "apphost", "DevelopmentServices.cs"));

        appHost.Should().Contain($"SeaweedFsImageTag = \"{SeaweedFsServer.Version}\"");
    }
}
