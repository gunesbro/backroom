using AwesomeAssertions;
using Backroom.Testing.Mailpit;

namespace Backroom.Testing.UnitTests;

/// <summary>The email tests must run the Mailpit that development runs, so its greeting (the Mailpit guard) is the same.</summary>
public sealed class MailpitVersionTests
{
    [Fact]
    public void AppHostImage_ComparedWithTheTestContainer_IsTheSameVersion()
    {
        var appHost = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "apphost", "DevelopmentServices.cs"));

        appHost.Should().Contain($"MailpitImageTag = \"{MailpitServer.Version}\"");
    }
}
