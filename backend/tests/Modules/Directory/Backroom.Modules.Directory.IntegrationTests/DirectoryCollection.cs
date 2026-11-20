namespace Backroom.Modules.Directory.IntegrationTests;

/// <summary>
/// Every class that signs in to the shared Keycloak realm. Each sign-in changes the realm (it enables the password
/// grant on the web client), and Keycloak answers 409 when two requests change the realm at the same time; the
/// creation limit also counts an account's organizations. So these classes run one at a time.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public static class DirectoryCollection
{
    public const string Name = "Directory organizations";
}
