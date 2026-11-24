namespace Backroom.Modules.Presence.Infrastructure;

/// <summary>
/// The Lua scripts that change presence atomically in Valkey (every node may run them at once). Times are Unix milliseconds passed in
/// by the caller (its <see cref="TimeProvider"/>), never Valkey's clock, so tests can drive them; Valkey's own expiries only clean up.
/// </summary>
internal static class PresenceScripts
{
    /// <summary>
    /// Evaluates one person. KEYS: connections hash, published record, due set, status copy, watch set. ARGV: account id, now, idle
    /// after, event interval, record lifetime, connection key grace, most organizations. Connection fields are
    /// <c>{expiresAt}:{lastActiveAt}:{idle 0|1}</c>; expired ones are deleted. The state is online when a connection is active and had
    /// input within the idle time, idle when connections remain, offline otherwise. The published record is
    /// <c>{state}:{statusVersion}:{nextAllowedAt}</c>: when the state or the status version differs from it and the event interval has
    /// passed, the record is replaced and the script returns <c>{state + 1, organizations, {dnd, emoji, text, expiresAt}}</c> for the
    /// caller to publish; otherwise <c>{0}</c>. Either way the person is due again at the earliest of the next connection expiry, the
    /// next idle time and the end of the event interval, or leaves the due set when nothing can change by itself.
    /// </summary>
    public const string Evaluate = """
        local now = tonumber(ARGV[2])
        local idleAfter = tonumber(ARGV[3])
        local fields = redis.call('HGETALL', KEYS[1])
        local state = 0
        local nextDue = false
        local maxExp = 0
        for i = 1, #fields, 2 do
          local exp, last, idle = string.match(fields[i + 1], '^(%d+):(%d+):([01])$')
          exp = tonumber(exp)
          if exp == nil or exp <= now then
            redis.call('HDEL', KEYS[1], fields[i])
          else
            if exp > maxExp then maxExp = exp end
            if not nextDue or exp < nextDue then nextDue = exp end
            local s = 1
            local idleAt = tonumber(last) + idleAfter
            if idle == '0' and idleAt > now then
              s = 2
              if idleAt < nextDue then nextDue = idleAt end
            end
            if s > state then state = s end
          end
        end
        if maxExp > 0 then redis.call('PEXPIREAT', KEYS[1], maxExp + tonumber(ARGV[6])) end
        local current = state .. ':' .. (redis.call('HGET', KEYS[4], 'v') or '0')
        local published = '0:0'
        local allowed = 0
        local record = redis.call('GET', KEYS[2])
        if record then
          local a, b, c = string.match(record, '^(%d+):(%d+):(%d+)$')
          if a then
            published = a .. ':' .. b
            allowed = tonumber(c)
          end
        end
        if published ~= current then
          if allowed <= now then
            redis.call('SET', KEYS[2], current .. ':' .. (now + tonumber(ARGV[4])), 'PX', ARGV[5])
            if nextDue then redis.call('ZADD', KEYS[3], nextDue, ARGV[1]) else redis.call('ZREM', KEYS[3], ARGV[1]) end
            redis.call('ZREMRANGEBYSCORE', KEYS[5], '-inf', now)
            local orgs = redis.call('ZRANGE', KEYS[5], 0, tonumber(ARGV[7]) - 1)
            local status = redis.call('HMGET', KEYS[4], 'dnd', 'emoji', 'text', 'exp')
            return {state + 1, orgs, status}
          end
          if not nextDue or allowed < nextDue then nextDue = allowed end
        end
        if nextDue then redis.call('ZADD', KEYS[3], nextDue, ARGV[1]) else redis.call('ZREM', KEYS[3], ARGV[1]) end
        return {0}
        """;

    /// <summary>
    /// Claims the people due by now. KEYS: due set. ARGV: now, most, lease. Each one claimed is moved to now + lease, so another node
    /// takes it only if this one fails to evaluate it in time; the evaluation sets the real next time.
    /// </summary>
    public const string Claim = """
        local ids = redis.call('ZRANGEBYSCORE', KEYS[1], '-inf', ARGV[1], 'LIMIT', 0, tonumber(ARGV[2]))
        local lease = tonumber(ARGV[1]) + tonumber(ARGV[3])
        for _, id in ipairs(ids) do redis.call('ZADD', KEYS[1], lease, id) end
        return ids
        """;

    /// <summary>
    /// Stores an account status copy if its version is newer. KEYS: status copy, due set. ARGV: version, dnd, emoji, text, expires at,
    /// time zone, retention, account id, now. Returns 1 when stored (the person becomes due now, so watchers hear of it), 0 otherwise
    /// (the copy's expiry is renewed).
    /// </summary>
    public const string ApplyStatus = """
        local held = tonumber(redis.call('HGET', KEYS[1], 'v') or '0')
        if tonumber(ARGV[1]) <= held then
          redis.call('PEXPIRE', KEYS[1], ARGV[7])
          return 0
        end
        redis.call('DEL', KEYS[1])
        redis.call('HSET', KEYS[1], 'v', ARGV[1], 'dnd', ARGV[2], 'emoji', ARGV[3], 'text', ARGV[4], 'exp', ARGV[5], 'tz', ARGV[6])
        redis.call('PEXPIRE', KEYS[1], ARGV[7])
        redis.call('ZADD', KEYS[2], 'LT', ARGV[9], ARGV[8])
        return 1
        """;

    /// <summary>
    /// Records watches of one organization. KEYS: the watch set of each watched person. ARGV: organization id, expires at, now, set
    /// lifetime, most organizations. Expired entries are dropped, and a set over the limit loses the entries closest to expiry.
    /// </summary>
    public const string Watch = """
        for i = 1, #KEYS do
          redis.call('ZADD', KEYS[i], ARGV[2], ARGV[1])
          redis.call('ZREMRANGEBYSCORE', KEYS[i], '-inf', ARGV[3])
          local extra = redis.call('ZCARD', KEYS[i]) - tonumber(ARGV[5])
          if extra > 0 then redis.call('ZREMRANGEBYRANK', KEYS[i], 0, extra - 1) end
          redis.call('PEXPIRE', KEYS[i], ARGV[4])
        end
        return #KEYS
        """;
}
