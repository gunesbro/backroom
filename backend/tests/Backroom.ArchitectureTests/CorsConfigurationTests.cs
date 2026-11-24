using System.Text.Json;
using AwesomeAssertions;

namespace Backroom.ArchitectureTests;

public sealed class CorsConfigurationTests
{
    [Fact]
    public void ConfigurationFiles_WhenCommitted_AllowNoWildcardOrigin()
    {
        var separator = Path.DirectorySeparatorChar;
        var files = Directory.EnumerateFiles(RepositoryPaths.SourceDirectory, "appsettings*.json", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{separator}bin{separator}", StringComparison.Ordinal)
                && !path.Contains($"{separator}obj{separator}", StringComparison.Ordinal))
            .ToList();

        files.Should().NotBeEmpty();

        var findings = files
            .Where(path => HasWildcardCors(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(RepositoryPaths.BackendDirectory, path))
            .ToList();

        findings.Should().BeEmpty("CORS origins are exact origins (docs/contracts/phase-1.md, Cross-cutting rules)");
    }

    [Theory]
    [InlineData("Backroom.Cell.Host")]
    [InlineData("Backroom.ControlPlane.Host")]
    public void Host_WhenBuildingThePipeline_AnswersCorsBeforeAuthentication(string host)
    {
        // A preflight carries no token; if authentication ran first it would be answered 401.
        var program = File.ReadAllText(Path.Combine(RepositoryPaths.SourceDirectory, "Hosts", host, "Program.cs"));

        var cors = program.IndexOf("app.UseWebOriginsCors()", StringComparison.Ordinal);
        var authentication = program.IndexOf("app.UseAuthentication()", StringComparison.Ordinal);

        cors.Should().BeGreaterThan(0);
        authentication.Should().BeGreaterThan(cors);
    }

    [Theory]
    [InlineData("""{ "Cors": { "AllowedOrigins": [ "*" ] } }""", true)]
    [InlineData("""{ "Cors": { "AllowedOrigins": [ "https://*.example.test" ] } }""", true)]
    [InlineData("""{ "Cors": { "AllowedOrigins": [ "https://app.example.test" ] } }""", false)]
    [InlineData("""{ "Logging": { "Note": "*" } }""", false)]
    public void HasWildcardCors_ReadsOnlyTheCorsSection(string json, bool expected)
    {
        HasWildcardCors(json).Should().Be(expected);
    }

    private static bool HasWildcardCors(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        return document.RootElement.TryGetProperty("Cors", out var cors) && ContainsWildcard(cors);
    }

    private static bool ContainsWildcard(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString()!.Contains('*', StringComparison.Ordinal),
        JsonValueKind.Array => element.EnumerateArray().Any(ContainsWildcard),
        JsonValueKind.Object => element.EnumerateObject().Any(property => ContainsWildcard(property.Value)),
        _ => false,
    };
}
