namespace Backroom.BuildingBlocks.Presentation.Authentication;

/// <summary>Claim names and values in Keycloak access tokens (inbound claim mapping is off).</summary>
public static class KeycloakClaims
{
    public const string Subject = "sub";

    public const string SessionId = "sid";

    public const string Email = "email";

    public const string EmailVerified = "email_verified";

    /// <summary>The person's full name (profile scope).</summary>
    public const string Name = "name";

    /// <summary>The person's BCP 47 locale, if set in Keycloak.</summary>
    public const string Locale = "locale";

    /// <summary>The client the token was issued to.</summary>
    public const string AuthorizedParty = "azp";

    /// <summary>Token type in the payload: <c>Bearer</c> for access tokens, <c>ID</c>, <c>Refresh</c> or <c>Logout</c> otherwise.</summary>
    public const string TokenType = "typ";

    public const string AccessTokenType = "Bearer";

    /// <summary>JSON object <c>{"roles": [...]}</c> with the realm roles.</summary>
    public const string RealmAccess = "realm_access";

    /// <summary>Authentication methods used for the session, e.g. <c>pwd</c> and <c>otp</c>.</summary>
    public const string AuthenticationMethods = "amr";

    public const string OneTimePassword = "otp";

    public const string OperationsRole = "ops";
}
