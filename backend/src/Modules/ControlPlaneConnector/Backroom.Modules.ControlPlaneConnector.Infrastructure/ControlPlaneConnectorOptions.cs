namespace Backroom.Modules.ControlPlaneConnector.Infrastructure;

/// <summary>How the cell reaches and authenticates to the control plane.</summary>
public sealed class ControlPlaneConnectorOptions
{
    public const string SectionName = "ControlPlaneConnector";

    /// <summary>This cell's id (the host's <c>Cell:Id</c>).</summary>
    public string CellId { get; set; } = string.Empty;

    /// <summary>The Keycloak realm URL (the host's <c>Authentication:Authority</c>); the cell's tokens come from it.</summary>
    public string Authority { get; set; } = string.Empty;

    public Uri? ControlPlaneUrl { get; set; }

    /// <summary>
    /// The one-time registration key, needed only until the cell is registered. A secret: from the
    /// deployment's secret store or, in development, from the AppHost.
    /// </summary>
    public string? RegistrationKey { get; set; }

    /// <summary>
    /// Where the cell's private key is kept. Production: a directory mounted from the secret store.
    /// Default (development): <c>%APPDATA%/Backroom/cells/{CellId}</c> in the user profile.
    /// </summary>
    public string? KeyDirectory { get; set; }

    /// <summary>How often a worker that does not hold the link checks whether it can take it over.</summary>
    public TimeSpan LinkLeaseRetryInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>The relay role's connection (the host's <c>OutboxRelay:ConnectionString</c>), for link delivery. A secret.</summary>
    public string RelayConnectionString { get; set; } = string.Empty;
}
