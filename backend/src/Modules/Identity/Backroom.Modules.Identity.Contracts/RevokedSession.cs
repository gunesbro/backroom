namespace Backroom.Modules.Identity.Contracts;

/// <summary>
/// A revoked session (<paramref name="SessionId"/> is the Keycloak <c>sid</c>) or, when it is <see langword="null"/>,
/// every session of the account that was issued up to <paramref name="RevokedAt"/>. Cells keep it until <paramref name="KeepUntil"/>.
/// </summary>
public sealed record RevokedSession(Guid AccountId, string? SessionId, DateTimeOffset RevokedAt, DateTimeOffset KeepUntil);
