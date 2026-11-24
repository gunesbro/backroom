namespace Backroom.Modules.Realtime.Contracts;

/// <summary>
/// One revocation: the Keycloak session <paramref name="SessionId"/> (<c>sid</c>) of <paramref name="AccountId"/>, or, when it is
/// <see langword="null"/>, every session of the account whose token was issued up to <paramref name="RevokedAt"/>. The cell keeps it
/// until <paramref name="KeepUntil"/>.
/// </summary>
public sealed record SessionRevocation(Guid AccountId, string? SessionId, DateTimeOffset RevokedAt, DateTimeOffset KeepUntil);
