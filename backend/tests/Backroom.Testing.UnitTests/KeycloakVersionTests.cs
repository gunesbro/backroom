using AwesomeAssertions;
using Backroom.Testing.Keycloak;

namespace Backroom.Testing.UnitTests;

/// <summary>
/// The Keycloak version is written in three places: the AppHost's image tag, the test container's image and the
/// upstream template hashes. If they drift apart, the template guard checks the wrong Keycloak and passes silently.
/// </summary>
public sealed class KeycloakVersionTests
{
    private static readonly string s_output = AppContext.BaseDirectory;
    private static readonly string s_hashes = Path.Combine(s_output, "keycloak", "theme", "upstream-templates.sha256");

    [Fact]
    public void AppHostImage_ComparedWithTheTestContainer_IsTheSameVersion()
    {
        var appHost = File.ReadAllText(Path.Combine(s_output, "apphost", "DevelopmentServices.cs"));

        appHost.Should().Contain(
            $"KeycloakImageTag = \"{KeycloakRealm.Version}\"",
            "the tests and the template guard must run the Keycloak that development runs");
    }

    [Fact]
    public void UpstreamTemplateHashes_ComparedWithTheTestContainer_AreForTheSameVersion()
    {
        UpstreamLoginTemplates.KeycloakVersion(s_hashes).Should().Be(
            KeycloakRealm.Version,
            "after an upgrade, the hashes are checked and re-recorded for the new version");
    }

    [Fact]
    public void EveryCopiedTemplate_IsListedWithItsUpstreamHash()
    {
        var templates = Directory.GetFiles(Path.Combine(s_output, "keycloak", "theme"), "*.ftl")
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal);

        templates.Should().Equal(
            UpstreamLoginTemplates.Read(s_hashes).Select(entry => entry.Name).Order(StringComparer.Ordinal),
            "a template copied into the theme needs its upstream hash so upgrades are checked");
    }
}
