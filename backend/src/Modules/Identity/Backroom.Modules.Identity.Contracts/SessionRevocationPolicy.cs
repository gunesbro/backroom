namespace Backroom.Modules.Identity.Contracts;

/// <summary>How long a revocation must be kept (docs/contracts/phase-1.md, "Identity and tokens").</summary>
public static class SessionRevocationPolicy
{
    /// <summary>
    /// The longest an access token lives. The realm's <c>accessTokenLifespan</c> is 300 s; a test pins that it does not
    /// exceed this, so a longer lifetime cannot silently outlive the revocations.
    /// </summary>
    public static readonly TimeSpan LongestAccessTokenLifetime = TimeSpan.FromMinutes(5);

    /// <summary>The clock difference the cells tolerate when they check a token's expiry (30 s, as <c>AccessTokenAuthenticationExtensions.ClockSkew</c>).</summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A revocation is kept until <c>revoked_at + KeepFor</c>: a token issued before the revocation expires within the
    /// longest lifetime, plus the skew the cells allow.
    /// </summary>
    public static readonly TimeSpan KeepFor = LongestAccessTokenLifetime + ClockSkew;
}
