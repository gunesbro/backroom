namespace Backroom.Modules.CellManagement.Infrastructure.Keycloak;

/// <summary>
/// How the control plane manages cell clients in Keycloak. The secret comes from user secrets or the
/// environment, never from appsettings files.
/// </summary>
internal sealed class KeycloakAdminOptions
{
    public const string SectionName = "CellManagement:Keycloak";

    /// <summary>The realm URL, also the issuer of cell tokens (the hosts' <c>Authentication:Authority</c>).</summary>
    public string Authority { get; set; } = string.Empty;

    public string AdminClientId { get; set; } = "control-plane-admin";

    public string AdminClientSecret { get; set; } = string.Empty;

    /// <summary>
    /// The clients people sign in with; each gets every cell's scope as an optional scope. They must match the
    /// clients the hosts accept (<c>KeycloakClients</c> in the building blocks).
    /// </summary>
    public string WebClientId { get; set; } = "web";

    /// <inheritdoc cref="WebClientId"/>
    public string DesktopClientId { get; set; } = "desktop";
}
