namespace Backroom.ArchitectureTests;

/// <summary>
/// Object storage sits behind Files' <c>IFileStorage</c> (CLAUDE.md, "Architecture rules": every external service
/// behind our own interface): only the Files module's infrastructure may use an object storage SDK. Other modules
/// store files through Files' contracts, so access checks and signed URLs cannot be bypassed.
/// </summary>
public static class ObjectStorageUsageRules
{
    public const string FilesInfrastructure = "Backroom.Modules.Files.Infrastructure";

    /// <summary>Assembly or package name prefixes of object storage SDKs.</summary>
    public static IReadOnlyCollection<string> StorageLibraryPrefixes { get; } =
    [
        "AWSSDK.S3",
        "Minio",
        "Azure.Storage.Blobs",
        "Google.Cloud.Storage",
        "Oci.ObjectstorageService",
    ];

    public static bool MayUseObjectStorage(string assemblyName) =>
        string.Equals(assemblyName, FilesInfrastructure, StringComparison.Ordinal);

    public static bool IsStorageLibrary(string name) =>
        StorageLibraryPrefixes.Any(prefix =>
            string.Equals(name, prefix, StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<string> FindViolations(string assemblyName, IEnumerable<string> referencedNames)
    {
        if (MayUseObjectStorage(assemblyName))
        {
            return [];
        }

        return referencedNames
            .Where(IsStorageLibrary)
            .Select(name => $"{assemblyName} must not use {name}; only {FilesInfrastructure} talks to object storage.")
            .ToList();
    }
}
