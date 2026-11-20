using Microsoft.AspNetCore.Http;
using Backroom.BuildingBlocks.Application.Accounts;

namespace Backroom.BuildingBlocks.Presentation.Authentication;

/// <summary>
/// The account of the current HTTP request; the token was validated by the authentication handler.
/// Only a person's token (with a session) has an account; a cell's service account never passes as one.
/// </summary>
internal sealed class HttpCurrentAccount(IHttpContextAccessor httpContextAccessor) : ICurrentAccount
{
    public Guid AccountId =>
        httpContextAccessor.HttpContext?.User is { } user
        && !string.IsNullOrEmpty(user.FindFirst(KeycloakClaims.SessionId)?.Value)
        && Guid.TryParse(user.FindFirst(KeycloakClaims.Subject)?.Value, out var accountId)
            ? accountId
            : throw new InvalidOperationException("No account is signed in for this request.");

    public string? SessionId => Claim(KeycloakClaims.SessionId);

    public string? VerifiedEmail =>
        string.Equals(Claim(KeycloakClaims.EmailVerified), "true", StringComparison.OrdinalIgnoreCase) ? Claim(KeycloakClaims.Email) : null;

    public string? DisplayName => Claim(KeycloakClaims.Name);

    public string? Locale => Claim(KeycloakClaims.Locale);

    private string? Claim(string type) => httpContextAccessor.HttpContext?.User.FindFirst(type)?.Value is { Length: > 0 } value ? value : null;
}
