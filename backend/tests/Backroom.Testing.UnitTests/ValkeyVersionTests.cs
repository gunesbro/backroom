using AwesomeAssertions;
using Backroom.Testing.Valkey;

namespace Backroom.Testing.UnitTests;

/// <summary>The live event tests must run the Valkey that development runs, so the backplane behaves the same.</summary>
public sealed class ValkeyVersionTests
{
    [Fact]
    public void AppHostImage_ComparedWithTheTestContainer_IsTheSameVersion()
    {
        var appHost = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "apphost", "DevelopmentServices.cs"));

        appHost.Should().Contain($"ValkeyImageTag = \"{ValkeyServer.Version}\"");
    }
}
