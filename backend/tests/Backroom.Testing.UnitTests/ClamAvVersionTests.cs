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
        appHost.Should().Contain($"ClamAvImageDigest = \"{ClamAvServer.Digest}\"");
        appHost.Should().Contain("CLAMD_CONF_AlertExceedsMax").And.Contain("CLAMD_CONF_AlertEncrypted").And.Contain("127.0.0.1:");
    }

    [Fact]
    public void AppHostImage_Pinned_IsNeverLatest()
    {
        ClamAvServer.Version.Should().MatchRegex(@"^\d+\.\d+\.\d+$");
    }
}
