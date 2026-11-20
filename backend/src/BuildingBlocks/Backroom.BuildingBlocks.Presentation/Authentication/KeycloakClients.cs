namespace Backroom.BuildingBlocks.Presentation.Authentication;

/// <summary>Keycloak clients people sign in with (the token's <c>azp</c>).</summary>
public static class KeycloakClients
{
    /// <summary>The web app.</summary>
    public const string Web = "web";

    /// <summary>The desktop app (system browser sign-in with a loopback redirect).</summary>
    public const string Desktop = "desktop";

    /// <summary>The operations panel.</summary>
    public const string Operations = "ops";
}
