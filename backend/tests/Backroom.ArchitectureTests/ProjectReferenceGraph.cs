using System.Xml.Linq;

namespace Backroom.ArchitectureTests;

/// <summary>
/// Reads the declared ProjectReference graph of every production project under backend/src.
/// Unlike compiled metadata, this also sees references that are not used yet and projects
/// that the test project does not reference.
/// </summary>
internal static class ProjectReferenceGraph
{
    private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlyList<string>>> s_projects = new(() => Load());

    /// <summary>Project name → names of the projects it references.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Projects => s_projects.Value;

    private static Dictionary<string, IReadOnlyList<string>> Load()
    {
        return Directory.EnumerateFiles(RepositoryPaths.SourceDirectory, "*.csproj", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetFileNameWithoutExtension(path),
                path => (IReadOnlyList<string>)XDocument.Load(path)
                    .Descendants("ProjectReference")
                    .Select(reference => reference.Attribute("Include")?.Value)
                    .OfType<string>()
                    .Select(include => Path.GetFileNameWithoutExtension(include.Replace('\\', '/')))
                    .ToList(),
                StringComparer.Ordinal);
    }
}
