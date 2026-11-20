namespace Backroom.ArchitectureTests;

internal static class RepositoryPaths
{
    private static readonly Lazy<string> s_backendDirectory = new(FindBackendDirectory);

    /// <summary>The backend directory, located by walking up from the test output to Backroom.slnx.</summary>
    public static string BackendDirectory => s_backendDirectory.Value;

    public static string SourceDirectory => Path.Combine(BackendDirectory, "src");

    private static string FindBackendDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Backroom.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Could not find Backroom.slnx above the test output directory.");
    }
}
