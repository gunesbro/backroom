namespace Backroom.BuildingBlocks.Presentation.Authentication;

/// <summary>
/// Where access tokens come from. The audience is not configurable: each host passes its own
/// (<see cref="Audiences"/>) and accepts only tokens whose single audience it is.
/// </summary>
public sealed class AccessTokenOptions
{
    public const string SectionName = "Authentication";

    /// <summary>The Keycloak realm URL, e.g. <c>https://id.example.test/realms/backroom</c>; also the token issuer.</summary>
    public string Authority { get; set; } = string.Empty;
}
