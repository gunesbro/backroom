namespace Backroom.Modules.CellManagement.IntegrationTests;

/// <summary>
/// Every class that signs in to the shared Keycloak realm with a password. Each sign-in changes the realm (it enables
/// the password grant on the web client), and Keycloak answers 409 when two requests change the realm at the same
/// time. So these classes run one at a time.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public static class KeycloakSignInCollection
{
    public const string Name = "Keycloak sign-ins";
}
