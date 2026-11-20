using System.Text.Json;
using System.Text.RegularExpressions;

namespace Backroom.ArchitectureTests;

/// <summary>
/// Finds secret-looking settings with values in committed configuration files.
/// Secrets come from user secrets, environment variables or Aspire parameters, never from these files.
/// </summary>
public static partial class ConfigurationSecrets
{
    [GeneratedRegex("(password|passwd|secret|apikey|api_key|token|privatekey|private_key|connectionstring)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretKeyPattern();

    /// <summary>Returns the JSON paths of secret-looking keys that have a non-empty value.</summary>
    public static IReadOnlyList<string> FindSecretValues(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });

        var findings = new List<string>();
        Visit(document.RootElement, "$", insideConnectionStrings: false, findings);
        return findings;
    }

    private static void Visit(JsonElement element, string path, bool insideConnectionStrings, List<string> findings)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var childPath = $"{path}.{property.Name}";
                    var isSecretKey = insideConnectionStrings || SecretKeyPattern().IsMatch(property.Name);

                    if (isSecretKey && HasValue(property.Value))
                    {
                        findings.Add(childPath);
                    }

                    var childInsideConnectionStrings = insideConnectionStrings
                        || string.Equals(property.Name, "ConnectionStrings", StringComparison.OrdinalIgnoreCase);
                    Visit(property.Value, childPath, childInsideConnectionStrings, findings);
                }

                break;

            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    Visit(item, $"{path}[{index++}]", insideConnectionStrings, findings);
                }

                break;
        }
    }

    // Only strings can hold secrets; numbers and booleans (e.g. TokenLifetimeMinutes) are settings.
    private static bool HasValue(JsonElement value) =>
        value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString());
}
