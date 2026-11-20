namespace Backroom.BuildingBlocks.Application.Accounts;

/// <summary>
/// The signed-in account of the current request, from its validated access token. An account is
/// global (one per person across organizations); organization membership is checked separately.
/// </summary>
public interface ICurrentAccount
{
    /// <summary>The account id (the token's <c>sub</c>, Keycloak's user id). Throws if no one is signed in.</summary>
    Guid AccountId { get; }

    /// <summary>The Keycloak session (<c>sid</c>), used to reject tokens of revoked sessions.</summary>
    string? SessionId { get; }

    /// <summary>The account's email if Keycloak verified it; otherwise <see langword="null"/>.</summary>
    string? VerifiedEmail { get; }

    /// <summary>The person's full name from the token, if any.</summary>
    string? DisplayName { get; }

    /// <summary>The person's BCP 47 locale from the token, if any.</summary>
    string? Locale { get; }
}
