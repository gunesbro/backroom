namespace Backroom.Modules.Realtime.Infrastructure.Revocation;

/// <summary>The answer to "may this token still be used?".</summary>
internal enum RevocationVerdict
{
    /// <summary>Neither the session nor the account is revoked for this token.</summary>
    Valid,

    /// <summary>The session, or the account for tokens issued before the revocation, is revoked.</summary>
    Revoked,

    /// <summary>Valkey could not be asked and no recent answer is at hand: the check fails closed.</summary>
    Unavailable,
}
