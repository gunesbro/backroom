using System.Text.RegularExpressions;

namespace Backroom.Testing.Keycloak;

/// <summary>
/// Reads <c>upstream-templates.sha256</c> next to our login theme: the Keycloak version in its header and, per line,
/// the SHA-256 of a keycloak.v2 login template our theme copies, written as "&lt;hash&gt;  &lt;file name&gt;".
/// </summary>
public static partial class UpstreamLoginTemplates
{
    public static IReadOnlyList<(string Name, string Sha256)> Read(string path)
    {
        var entries = new List<(string, string)>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
            {
                continue;
            }

            var match = EntryPattern().Match(line.Trim());
            if (!match.Success)
            {
                throw new FormatException($"Expected \"<sha256>  <template>.ftl\" in {Path.GetFileName(path)}, found \"{line}\".");
            }

            entries.Add((match.Groups["name"].Value, match.Groups["hash"].Value));
        }

        return entries;
    }

    /// <summary>The Keycloak version the hashes were taken from, as the file's header names it.</summary>
    public static string KeycloakVersion(string path)
    {
        var match = VersionPattern().Match(File.ReadAllText(path));
        return match.Success ? match.Groups["version"].Value : throw new FormatException($"No Keycloak version in {Path.GetFileName(path)}.");
    }

    [GeneratedRegex(@"^(?<hash>[0-9a-f]{64}) {2}(?<name>[A-Za-z0-9._-]+\.ftl)$", RegexOptions.CultureInvariant)]
    private static partial Regex EntryPattern();

    [GeneratedRegex(@"^# .*\bKeycloak (?<version>\d+\.\d+\.\d+)\b", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex VersionPattern();
}
