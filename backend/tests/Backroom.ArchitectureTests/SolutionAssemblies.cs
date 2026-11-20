using System.Reflection;

namespace Backroom.ArchitectureTests;

/// <summary>
/// Loads the production assemblies copied next to the test assembly by its project references.
/// </summary>
internal static class SolutionAssemblies
{
    private static readonly Lazy<IReadOnlyList<Assembly>> s_all = new(() => Load());

    public static IReadOnlyList<Assembly> All => s_all.Value;

    public static Assembly Get(string name) =>
        All.Single(assembly => string.Equals(assembly.GetName().Name, name, StringComparison.Ordinal));

    private static List<Assembly> Load()
    {
        var directory = AppContext.BaseDirectory;
        return Directory.EnumerateFiles(directory, "Backroom.*.dll")
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .Where(name => !name.StartsWith("Backroom.ArchitectureTests", StringComparison.Ordinal)
                && !name.StartsWith("Backroom.Testing", StringComparison.Ordinal))
            .Select(name => Assembly.Load(new AssemblyName(name)))
            .OrderBy(assembly => assembly.GetName().Name, StringComparer.Ordinal)
            .ToList();
    }
}
