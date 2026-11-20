namespace Backroom.ArchitectureTests;

/// <summary>
/// Database access (EF Core, Npgsql) belongs to Infrastructure layers ("Repo ve klasör yapısı" in
/// docs/requirements.md). Domain, Application, Contracts and Presentation reach data through
/// application interfaces, so the unit of work and row-level security cannot be bypassed from them.
/// </summary>
public static class DataAccessRules
{
    private static readonly string[] s_dataAccessPrefixes = ["Npgsql", "Microsoft.EntityFrameworkCore"];

    private static readonly string[] s_layersWithoutDataAccess = [".Domain", ".Application", ".Contracts", ".Presentation"];

    public static IReadOnlyList<string> FindViolations(string assemblyName, IEnumerable<string> referencedNames)
    {
        if (!s_layersWithoutDataAccess.Any(layer => assemblyName.EndsWith(layer, StringComparison.Ordinal)))
        {
            return [];
        }

        return referencedNames
            .Where(name => s_dataAccessPrefixes.Any(prefix =>
                string.Equals(name, prefix, StringComparison.Ordinal)
                || name.StartsWith(prefix + ".", StringComparison.Ordinal)))
            .Select(name => $"{assemblyName} must not reference {name}; database access belongs to Infrastructure.")
            .ToList();
    }
}
