using System.Globalization;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Backroom.Modules.Realtime.Contracts;
using Backroom.Modules.Realtime.Infrastructure.Gateway;

namespace Backroom.Modules.Realtime.Infrastructure.Revocation;

/// <summary>
/// The cell's revoked sessions in Valkey (docs/contracts/phase-1.md, "Identity and tokens"): one key per revoked session
/// (<c>backroom:cell:{cellId}:revoked:sid:{accountId}:{sid}</c>) and per account whose every session was revoked
/// (<c>...:revoked:account:{accountId}</c>), each with an expiry at the revocation's <c>keep_until</c> (never later than
/// <see cref="RealtimeOptions.RevocationMaxRetention"/>), so the set cleans itself and is bounded by the revocations of the
/// last few minutes. The cell id in the key keeps cells that share a server apart. The value is
/// <c>{revokedAtMs}:{keepUntilMs}</c>, nothing else (no email, no name).
/// </summary>
internal sealed class ValkeyRevocationStore(ValkeyConnection valkey, IOptions<RealtimeOptions> options, TimeProvider timeProvider) : IRevocationStore
{
    /// <summary>The longest session id the cell stores or looks up (Keycloak's are UUIDs).</summary>
    public const int MaxSessionIdLength = 128;

    /// <inheritdoc />
    public async Task StoreAsync(SessionRevocation revocation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(revocation);
        var settings = options.Value;
        var lifetime = revocation.KeepUntil - timeProvider.GetUtcNow();
        if (lifetime > settings.RevocationMaxRetention)
        {
            lifetime = settings.RevocationMaxRetention;
        }

        if (lifetime <= TimeSpan.Zero)
        {
            return;
        }

        var key = revocation.SessionId is { } sessionId ? SessionKey(revocation.AccountId, sessionId) : AccountKey(revocation.AccountId);
        var value = string.Create(
            CultureInfo.InvariantCulture,
            $"{revocation.RevokedAt.ToUnixTimeMilliseconds()}:{revocation.KeepUntil.ToUnixTimeMilliseconds()}");
        var database = (await valkey.GetAsync(cancellationToken)).GetDatabase();
        await database.StringSetAsync(key, value, lifetime, When.Always).WaitAsync(settings.CommandTimeout, timeProvider, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<(RevocationRecord? Session, RevocationRecord? Account)> GetAsync(string? sessionId, Guid accountId, CancellationToken cancellationToken)
    {
        var keys = sessionId is null
            ? new[] { AccountKey(accountId) }
            : [SessionKey(accountId, sessionId), AccountKey(accountId)];
        var database = (await valkey.GetAsync(cancellationToken)).GetDatabase();
        var values = await database.StringGetAsync(keys).WaitAsync(options.Value.CommandTimeout, timeProvider, cancellationToken);
        return sessionId is null
            ? (null, Parse(values[0]))
            : (Parse(values[0]), Parse(values[1]));
    }

    // A session is the account's: the same id under another account is another key.
    private RedisKey SessionKey(Guid accountId, string sessionId) => Prefix + "sid:" + accountId.ToString("N") + ":" + sessionId;

    private RedisKey AccountKey(Guid accountId) => Prefix + "account:" + accountId.ToString("N");

    private string Prefix => "backroom:cell:" + options.Value.CellId + ":revoked:";

    // A value this code did not write (someone with access to Valkey) counts as revoked for the next minute: failing
    // closed is the safe reading of an entry that exists but cannot be understood.
    private RevocationRecord? Parse(RedisValue value)
    {
        if (value.IsNull)
        {
            return null;
        }

        var text = (string?)value;
        var parts = text?.Split(':');
        if (parts is { Length: 2 }
            && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var revokedAt)
            && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var keepUntil)
            && revokedAt <= 253_402_300_799_000 && keepUntil <= 253_402_300_799_000)
        {
            return new RevocationRecord(DateTimeOffset.FromUnixTimeMilliseconds(revokedAt), DateTimeOffset.FromUnixTimeMilliseconds(keepUntil));
        }

        var now = timeProvider.GetUtcNow();
        return new RevocationRecord(now, now.AddMinutes(1));
    }
}
