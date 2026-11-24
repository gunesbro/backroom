using System.Text.Json;
using AwesomeAssertions;

namespace Backroom.ArchitectureTests;

/// <summary>
/// The hosting "Request starting" log line prints the query string, which holds <c>access_token</c> on the SignalR hub
/// path. The host turns that category off in code, but a provider-specific rule in configuration for the same category
/// would win over it, so no committed configuration may set that category below Warning.
/// </summary>
public sealed class HostLogFilterTests
{
    private const string Category = "Microsoft.AspNetCore.Hosting.Diagnostics";

    [Fact]
    public void AppSettings_WhenCommitted_NeverEnableTheHostingRequestLog()
    {
        var files = Directory.EnumerateFiles(RepositoryPaths.SourceDirectory, "appsettings*.json", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();

        files.Should().NotBeEmpty();
        var findings = files.SelectMany(path => FindLowLevels(File.ReadAllText(path)).Select(jsonPath => $"{Path.GetFileName(path)}: {jsonPath}")).ToList();

        findings.Should().BeEmpty($"{Category} would print access tokens from the query string");
    }

    [Theory]
    [InlineData("""{ "Logging": { "Console": { "LogLevel": { "Microsoft.AspNetCore.Hosting.Diagnostics": "Information" } } } }""", 1)]
    [InlineData("""{ "Logging": { "LogLevel": { "Microsoft.AspNetCore.Hosting.Diagnostics": "Debug" } } }""", 1)]
    [InlineData("""{ "Logging": { "LogLevel": { "Microsoft.AspNetCore.Hosting.Diagnostics": "Warning" } } }""", 0)]
    [InlineData("""{ "Logging": { "LogLevel": { "Microsoft.AspNetCore": "Warning", "Default": "Information" } } }""", 0)]
    public void FindLowLevels_WithTheCategory_FlagsOnlyLevelsBelowWarning(string json, int expected) =>
        FindLowLevels(json).Should().HaveCount(expected);

    internal static IReadOnlyList<string> FindLowLevels(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var findings = new List<string>();
        Visit(document.RootElement, "$", findings);
        return findings;
    }

    private static void Visit(JsonElement element, string path, List<string> findings)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var child = $"{path}.{property.Name}";
                if (string.Equals(property.Name, Category, StringComparison.Ordinal)
                    && property.Value.ValueKind == JsonValueKind.String
                    && property.Value.GetString() is "Trace" or "Debug" or "Information")
                {
                    findings.Add(child);
                }

                Visit(property.Value, child, findings);
            }
        }
    }
}
