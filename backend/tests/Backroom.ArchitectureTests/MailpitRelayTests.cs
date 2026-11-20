using System.Text.RegularExpressions;
using AwesomeAssertions;

namespace Backroom.ArchitectureTests;

/// <summary>
/// Mailpit must never send captured mail on (docs/adr/0008, item 6). In v1.32 three features do: relay
/// (<c>--smtp-relay-config</c>, <c>--smtp-relay-all</c>, <c>--smtp-relay-matching</c>, <c>MP_SMTP_RELAY_*</c>), which
/// releases or auto-relays messages through a real server; forwarding (<c>--smtp-forward-config</c>,
/// <c>MP_SMTP_FORWARD_*</c>), which copies every message to real addresses; and webhooks (<c>--webhook-url</c>,
/// <c>MP_WEBHOOK_*</c>), which post every message's sender, recipients and subject to a URL. Their configuration
/// files are only read through those flags or variables. None of them may appear in the backend's sources or in
/// deployment files (Compose, Helm values and templates, Dockerfiles, scripts).
/// </summary>
public sealed partial class MailpitRelayTests
{
    private static readonly string[] s_extensions = [".cs", ".json", ".yml", ".yaml", ".env", ".toml", ".tpl", ".txt", ".sh", ".ps1", ".conf", ".ini", ".dockerfile"];

    [Fact]
    public void Sources_AndDeploymentFiles_DoNotConfigureMailpitOutboundSending()
    {
        var roots = new[] { RepositoryPaths.SourceDirectory, Path.Combine(RepositoryPaths.BackendDirectory, "..", "deploy") }
            .Where(Directory.Exists);

        var offenders = roots
            .SelectMany(root => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            .Where(IsScanned)
            .Where(path => OutboundSetting().IsMatch(File.ReadAllText(path)))
            .ToList();

        offenders.Should().BeEmpty();
    }

    [Theory]
    [InlineData("MP_SMTP_RELAY_HOST")]
    [InlineData("MP_SMTP_RELAY_CONFIG")]
    [InlineData("mp_smtp_relay_all")]
    [InlineData("MP_SMTP_RELAY_MATCHING")]
    [InlineData("--smtp-relay-config")]
    [InlineData("--smtp-relay-all")]
    [InlineData("--smtp-relay-matching")]
    [InlineData("MP_SMTP_FORWARD_CONFIG")]
    [InlineData("MP_SMTP_FORWARD_TO")]
    [InlineData("MP_SMTP_FORWARD_HOST")]
    [InlineData("--smtp-forward-config")]
    [InlineData("MP_WEBHOOK_URL")]
    [InlineData("--webhook-url")]
    [InlineData("--webhook-limit")]
    [InlineData(".WithEnvironment(\"MP_SMTP_FORWARD_TO\", \"someone@example.com\")")]
    [InlineData("command: [\"--smtp-forward-config\", \"/forward.yml\"]")]
    [InlineData("- name: MP_SMTP_RELAY_ALL")]
    public void OutboundSetting_MatchesMailpitsRelayForwardAndWebhookOptions(string text)
    {
        OutboundSetting().IsMatch(text).Should().BeTrue();
    }

    [Theory]
    [InlineData("OutboxRelay__ConnectionString")]
    [InlineData("MP_MAX_MESSAGES")]
    [InlineData("MP_SMTP_AUTH_ACCEPT_ANY")]
    [InlineData("// Relay is never configured (docs/adr/0008).")]
    public void OutboundSetting_DoesNotMatchOtherSettings(string text)
    {
        OutboundSetting().IsMatch(text).Should().BeFalse();
    }

    [Theory]
    [InlineData("deploy/helm/templates/mailpit.tpl", true)]
    [InlineData("deploy/helm/values.yaml", true)]
    [InlineData("deploy/compose/compose.yml", true)]
    [InlineData("deploy/Dockerfile", true)]
    [InlineData("deploy/mailpit/Dockerfile.dev", true)]
    [InlineData("deploy/mailpit.Dockerfile", true)]
    [InlineData("deploy/.env", true)]
    [InlineData("deploy/.env.staging", true)]
    [InlineData("src/Aspire/Backroom.AppHost/DevelopmentServices.cs", true)]
    [InlineData("src/Hosts/Backroom.Cell.Host/obj/Debug/generated.cs", false)]
    [InlineData("deploy/logo.png", false)]
    public void IsScanned_CoversDeploymentFileTypes(string path, bool expected)
    {
        IsScanned(path.Replace('/', Path.DirectorySeparatorChar)).Should().Be(expected);
    }

    private static bool IsScanned(string path)
    {
        var separator = Path.DirectorySeparatorChar;
        if (path.Contains($"{separator}bin{separator}", StringComparison.Ordinal) || path.Contains($"{separator}obj{separator}", StringComparison.Ordinal))
        {
            return false;
        }

        var name = Path.GetFileName(path);
        return name.StartsWith("Dockerfile", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(".env", StringComparison.OrdinalIgnoreCase)
            || s_extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"MP_SMTP_RELAY|MP_SMTP_FORWARD|MP_WEBHOOK|--smtp-relay|--smtp-forward|--webhook", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OutboundSetting();
}
