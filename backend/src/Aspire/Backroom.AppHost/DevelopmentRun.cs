using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
        var root = configuration[KeyDirectoryRootKey];
        if (!isolated)
        {
            // A key root alone would move a developer's cell keys away from their registration; it belongs to isolated runs only.
            return string.IsNullOrEmpty(root)
                ? new DevelopmentRun(Isolated: false, KeyDirectoryRoot: null)
                : throw new InvalidOperationException($"{KeyDirectoryRootKey} is used only together with {IsolatedKey}=true.");
        }

        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
        {
            throw new InvalidOperationException($"An isolated run needs {KeyDirectoryRootKey}, an absolute directory path.");
        }

        return new DevelopmentRun(Isolated: true, KeyDirectoryRoot: root);
    }

    /// <summary>Warns at start when the run is isolated, so nobody mistakes it for the development environment.</summary>
    public void WarnIfIsolated(IDistributedApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (!Isolated)
        {
            return;
        }

        builder.Eventing.Subscribe<BeforeStartEvent>((@event, _) =>
        {
            s_logIsolatedRun(@event.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Backroom.AppHost"), null);
            return Task.CompletedTask;
        });
    }

    // Not the source generator: it does not run in the AppHost project.
    private static readonly Action<ILogger, Exception?> s_logIsolatedRun = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(1, "IsolatedRun"),
        "Isolated run: no data volumes, no LiveKit or object storage, cell keys under a temporary root. Not the development environment.");
}
