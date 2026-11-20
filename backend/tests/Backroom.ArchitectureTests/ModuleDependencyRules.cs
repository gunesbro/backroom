namespace Backroom.ArchitectureTests;

/// <summary>
/// Project dependency rules (see "Repo ve klasör yapısı" in docs/requirements.md).
/// Module projects are named Backroom.Modules.{Module}.{Layer}.
/// </summary>
/// <remarks>
/// Deviations from the requirements table, decided in Phase 0:
/// Infrastructure may also reference its own and other modules' Contracts (queue consumers need event contracts),
/// and Contracts may reference BuildingBlocks.Domain (shared primitives such as an integration event marker).
/// </remarks>
public static class ModuleDependencyRules
{
    public const string ModulePrefix = "Backroom.Modules.";
    public const string BuildingBlocksPrefix = "Backroom.BuildingBlocks.";
    public const string AppHostName = "Backroom.AppHost";
    public const string ControlPlaneHostName = "Backroom.ControlPlane.Host";
    public const string CellHostName = "Backroom.Cell.Host";
    public const string ServiceDefaultsName = "Backroom.ServiceDefaults";

    private const string Contracts = "Contracts";
    private const string Domain = "Domain";
    private const string Application = "Application";
    private const string Infrastructure = "Infrastructure";
    private const string Presentation = "Presentation";

    public static IReadOnlyCollection<string> KnownLayers { get; } =
        [Contracts, Domain, Application, Infrastructure, Presentation];

    // Layers of the same module that a layer may reference.
    private static readonly Dictionary<string, string[]> s_allowedOwnLayers = new(StringComparer.Ordinal)
    {
        [Contracts] = [],
        [Domain] = [],
        [Application] = [Domain, Contracts],
        [Infrastructure] = [Application, Domain, Contracts],
        [Presentation] = [Application, Contracts],
    };

    // Building blocks that a layer may reference.
    private static readonly Dictionary<string, string[]> s_allowedBuildingBlocks = new(StringComparer.Ordinal)
    {
        [Contracts] = [Domain],
        [Domain] = [Domain],
        [Application] = [Domain, Application],
        [Infrastructure] = [Domain, Application, Infrastructure],
        [Presentation] = [Domain, Application, Presentation],
    };

    // Building blocks that a building block may reference (the building blocks are layered too).
    private static readonly Dictionary<string, string[]> s_buildingBlockLayers = new(StringComparer.Ordinal)
    {
        [Domain] = [],
        [Application] = [Domain],
        [Infrastructure] = [Domain, Application],
        [Presentation] = [Domain, Application],
    };

    // Layers that may reference other modules' Contracts.
    private static readonly HashSet<string> s_mayUseOtherContracts = new(StringComparer.Ordinal)
    {
        Application,
        Infrastructure,
        Presentation,
    };

    public static bool IsModuleAssembly(string assemblyName) =>
        assemblyName.StartsWith(ModulePrefix, StringComparison.Ordinal);

    /// <summary>
    /// Returns a description of every forbidden reference from <paramref name="assemblyName"/>.
    /// Non-Backroom references (framework, NuGet packages) are not checked here.
    /// </summary>
    public static IReadOnlyList<string> FindViolations(string assemblyName, IEnumerable<string> referencedAssemblyNames)
    {
        var violations = new List<string>();
        var source = ParseModuleAssembly(assemblyName);

        if (IsModuleAssembly(assemblyName) && source is null)
        {
            violations.Add($"{assemblyName} does not follow Backroom.Modules.{{Module}}.{{Layer}} with a known layer.");
            return violations;
        }

        foreach (var reference in referencedAssemblyNames)
        {
            if (string.Equals(reference, AppHostName, StringComparison.Ordinal))
            {
                violations.Add($"{assemblyName} must not reference {AppHostName}.");
                continue;
            }

            // Service defaults configure the web host (error handling, telemetry); only hosts compose them.
            if (string.Equals(reference, ServiceDefaultsName, StringComparison.Ordinal))
            {
                if (!IsHost(assemblyName))
                {
                    violations.Add($"{assemblyName} must not reference {ServiceDefaultsName}; only hosts do.");
                }

                continue;
            }

            if (IsHost(reference))
            {
                if (!string.Equals(assemblyName, AppHostName, StringComparison.Ordinal))
                {
                    violations.Add($"{assemblyName} must not reference host {reference}.");
                }

                continue;
            }

            if (assemblyName.StartsWith(BuildingBlocksPrefix, StringComparison.Ordinal))
            {
                CheckBuildingBlockReference(assemblyName, reference, violations);
                continue;
            }

            if (source is null)
            {
                continue;
            }

            CheckModuleReference(assemblyName, source.Value, reference, violations);
        }

        return violations;
    }

    private static void CheckModuleReference(
        string assemblyName,
        (string Module, string Layer) source,
        string reference,
        List<string> violations)
    {
        var (sourceModule, sourceLayer) = source;

        if (reference.StartsWith(BuildingBlocksPrefix, StringComparison.Ordinal))
        {
            var block = reference[BuildingBlocksPrefix.Length..];
            if (!s_allowedBuildingBlocks[sourceLayer].Contains(block, StringComparer.Ordinal))
            {
                violations.Add($"{assemblyName} ({sourceLayer}) must not reference {reference}.");
            }

            return;
        }

        if (!IsModuleAssembly(reference))
        {
            return;
        }

        var target = ParseModuleAssembly(reference);
        if (target is null)
        {
            violations.Add($"{assemblyName} references {reference}, which is not a valid module project name.");
            return;
        }

        var (targetModule, targetLayer) = target.Value;

        if (string.Equals(sourceModule, targetModule, StringComparison.Ordinal))
        {
            if (!s_allowedOwnLayers[sourceLayer].Contains(targetLayer, StringComparer.Ordinal))
            {
                violations.Add($"{assemblyName} ({sourceLayer}) must not reference its own {targetLayer} layer.");
            }
        }
        else if (!string.Equals(targetLayer, Contracts, StringComparison.Ordinal))
        {
            violations.Add($"{assemblyName} may reference only the Contracts of module {targetModule}, not {reference}.");
        }
        else if (!s_mayUseOtherContracts.Contains(sourceLayer))
        {
            violations.Add($"{assemblyName} ({sourceLayer}) must not reference other modules.");
        }
    }

    private static void CheckBuildingBlockReference(string assemblyName, string reference, List<string> violations)
    {
        if (IsModuleAssembly(reference))
        {
            violations.Add($"{assemblyName} must not reference module {reference}.");
            return;
        }

        if (!reference.StartsWith(BuildingBlocksPrefix, StringComparison.Ordinal))
        {
            return;
        }

        var sourceLayer = assemblyName[BuildingBlocksPrefix.Length..];
        var targetLayer = reference[BuildingBlocksPrefix.Length..];
        if (!s_buildingBlockLayers.TryGetValue(sourceLayer, out var allowed)
            || !allowed.Contains(targetLayer, StringComparer.Ordinal))
        {
            violations.Add($"{assemblyName} must not reference {reference}.");
        }
    }

    private static bool IsHost(string assemblyName) =>
        string.Equals(assemblyName, ControlPlaneHostName, StringComparison.Ordinal)
        || string.Equals(assemblyName, CellHostName, StringComparison.Ordinal);

    private static (string Module, string Layer)? ParseModuleAssembly(string assemblyName)
    {
        if (!IsModuleAssembly(assemblyName))
        {
            return null;
        }

        var parts = assemblyName[ModulePrefix.Length..].Split('.');
        return parts.Length == 2 && KnownLayers.Contains(parts[1], StringComparer.Ordinal)
            ? (parts[0], parts[1])
            : null;
    }
}
