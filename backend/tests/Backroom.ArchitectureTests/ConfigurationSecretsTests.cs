using AwesomeAssertions;

namespace Backroom.ArchitectureTests;

public sealed class ConfigurationSecretsTests
{
    [Fact]
    public void ConfigurationFiles_WhenCommitted_ContainNoSecretValues()
    {
        var files = Directory.EnumerateFiles(RepositoryPaths.SourceDirectory, "*.json", SearchOption.AllDirectories)
            .Where(path => IsConfigurationFile(Path.GetFileName(path)))
            .Where(path => !IsBuildOutput(path))
            .ToList();

        files.Should().NotBeEmpty();

        var findings = files
            .SelectMany(path => ConfigurationSecrets.FindSecretValues(File.ReadAllText(path))
                .Select(jsonPath => $"{Path.GetRelativePath(RepositoryPaths.BackendDirectory, path)}: {jsonPath}"))
            .ToList();

        findings.Should().BeEmpty("secrets belong in user secrets, environment variables or Aspire parameters");
    }

    [Theory]
    [InlineData("""{ "Database": { "Password": "hunter2" } }""", "$.Database.Password")]
    [InlineData("""{ "LiveKit": { "ApiSecret": "abc" } }""", "$.LiveKit.ApiSecret")]
    [InlineData("""{ "ConnectionStrings": { "postgres": "Host=x;Password=y" } }""", "$.ConnectionStrings.postgres")]
    [InlineData("""{ "profiles": { "http": { "environmentVariables": { "GOOGLE_CLIENT_SECRET": "s" } } } }""", "$.profiles.http.environmentVariables.GOOGLE_CLIENT_SECRET")]
    public void FindSecretValues_WhenSecretHasValue_ReturnsPath(string json, string expectedPath)
    {
        ConfigurationSecrets.FindSecretValues(json).Should().Contain(expectedPath);
    }

    [Theory]
    [InlineData("""{ "Database": { "Password": "" } }""")]
    [InlineData("""{ "ConnectionStrings": { } }""")]
    [InlineData("""{ "Logging": { "LogLevel": { "Default": "Information" } } }""")]
    [InlineData("""{ "Tokens": { "Lifetime": null } }""")]
    [InlineData("""{ "Auth": { "TokenLifetimeMinutes": 5, "RequireToken": true } }""")]
    public void FindSecretValues_WhenNoSecretValue_ReturnsNothing(string json)
    {
        ConfigurationSecrets.FindSecretValues(json).Should().BeEmpty();
    }

    private static bool IsConfigurationFile(string fileName) =>
        fileName.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase)
        || string.Equals(fileName, "launchSettings.json", StringComparison.OrdinalIgnoreCase);

    private static bool IsBuildOutput(string path)
    {
        var separator = Path.DirectorySeparatorChar;
        return path.Contains($"{separator}bin{separator}", StringComparison.Ordinal)
            || path.Contains($"{separator}obj{separator}", StringComparison.Ordinal);
    }
}
