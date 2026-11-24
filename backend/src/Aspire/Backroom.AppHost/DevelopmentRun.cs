using Microsoft.Extensions.Configuration;

namespace Backroom.AppHost;

/// <summary>
/// How the AppHost runs. The default is the developer's environment: data volumes, every backing service and the fixed
/// ports of the launch profiles and <see cref="DevelopmentServices"/>. An isolated run (the end-to-end tests) can run next
/// to it: no data volumes, so every run starts from fresh containers; no containers whose host ports cannot be moved
/// (LiveKit publishes its ports directly) or that the tests do not use (object storage); and the cells' key directories
/// under a root the caller owns. The caller also randomizes the proxied ports (Aspire testing does).
/// </summary>
internal sealed record DevelopmentRun(bool Isolated, string? KeyDirectoryRoot)
{
    /// <summary>Configuration key (environment: <c>Backroom__Isolated</c>) for an isolated run; default false.</summary>
    public const string IsolatedKey = "Backroom:Isolated";

    /// <summary>Configuration key for the directory under which an isolated run keeps the cells' keys; required then.</summary>
    public const string KeyDirectoryRootKey = "Backroom:KeyDirectoryRoot";

    public static DevelopmentRun FromConfiguration(IConfiguration configuration)
    {
        var isolated = configuration.GetValue(IsolatedKey, defaultValue: false);
        if (!isolated)
        {
            return new DevelopmentRun(Isolated: false, KeyDirectoryRoot: null);
        }

        var root = configuration[KeyDirectoryRootKey];
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
        {
            throw new InvalidOperationException($"An isolated run needs {KeyDirectoryRootKey}, an absolute directory path.");
        }

        return new DevelopmentRun(Isolated: true, KeyDirectoryRoot: root);
    }
}
