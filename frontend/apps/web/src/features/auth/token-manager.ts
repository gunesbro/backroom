import {
  InvalidScopeError,
  ScopeNotGrantedError,
  SessionEndedError,
  targetsOf,
  type TokenTarget,
} from './cell-tokens';

/** A token endpoint answer to a refresh, already checked for shape. */
export type RefreshAnswer = {
  accessToken: string;
  refreshToken: string | undefined;
  expiresIn: number;
  scope: string;
};

/** Asks the token endpoint for one target; rejects with SessionEndedError on `invalid_grant`. */
export type Refresh = (refreshToken: string, scope: TokenTarget) => Promise<RefreshAnswer>;

/** A token this close to expiry is renewed first, so it cannot expire on its way to the API. */
const RENEW_BEFORE_MS = 30_000;
/** Keycloak issues 5-minute access tokens; a longer lifetime is not trusted. */
const MAX_LIFETIME_MS = 3_600_000;
/** The control plane, 20 shared and 10 remembered cells, and the one being added. */
const MAX_CACHED_TARGETS = 32;

export type TokenManager = {
  /** An access token for exactly one target, from the cache or one refresh. */
  get: (target: TokenTarget) => Promise<string>;
  /** Forgets every token (sign-out); a refresh still running is dropped when it answers. */
  clear: () => void;
};

/**
 * The web tab's tokens (contract, "Identity and tokens"): the refresh token stays in this closure, every refresh goes
 * through one queue because each use rotates it, and each access token names one target.
 */
export function createTokenManager({
  refreshToken: initialRefreshToken,
  granted,
  refresh,
  now = Date.now,
}: {
  refreshToken: string;
  /** The targets granted at sign-in (the code exchange's `scope`); Keycloak never adds others on refresh. */
  granted: ReadonlySet<TokenTarget>;
  refresh: Refresh;
  now?: () => number;
}): TokenManager {
  let refreshToken: string | undefined = initialRefreshToken;
  let generation = 0;
  const cache = new Map<TokenTarget, { token: string; validUntil: number }>();
  let queue: Promise<unknown> = Promise.resolve();

  function cached(target: TokenTarget): string | undefined {
    const entry = cache.get(target);
    return entry && now() + RENEW_BEFORE_MS < entry.validUntil ? entry.token : undefined;
  }

  function endSession() {
    refreshToken = undefined;
    cache.clear();
  }

  async function renew(target: TokenTarget, started: number): Promise<string> {
    if (started !== generation) throw new SessionEndedError();
    // Another request in the queue may have fetched it already.
    const hit = cached(target);
    if (hit) return hit;
    const current = refreshToken;
    if (!current) throw new SessionEndedError();
    if (!granted.has(target)) throw new ScopeNotGrantedError(target, false);

    let answer: RefreshAnswer;
    try {
      answer = await refresh(current, target);
    } catch (error) {
      // A refusal uses nothing up only if it is not invalid_grant; invalid_grant means the token is gone.
      if (error instanceof SessionEndedError && started === generation) endSession();
      throw error;
    }

    if (started !== generation) throw new SessionEndedError();
    // The old refresh token is used up whatever else the answer says, so the new one is kept first.
    if (!answer.refreshToken) {
      endSession();
      throw new SessionEndedError();
    }
    refreshToken = answer.refreshToken;

    // Keycloak silently leaves out a scope it cannot grant.
    const targets = targetsOf(answer.scope);
    if (!targets.has(target)) throw new ScopeNotGrantedError(target, true);
    // A token for more than one target could be replayed against the others; it is never used.
    if (targets.size > 1) throw new Error('The token answer names more than one target.');

    if (cache.size >= MAX_CACHED_TARGETS) {
      for (const [key, entry] of cache) if (entry.validUntil <= now()) cache.delete(key);
      if (cache.size >= MAX_CACHED_TARGETS) cache.clear();
    }
    const lifetime = Math.min(Math.max(answer.expiresIn, 0) * 1000, MAX_LIFETIME_MS);
    cache.set(target, { token: answer.accessToken, validUntil: now() + lifetime });
    return answer.accessToken;
  }

  return {
    get: (target) => {
      const hit = cached(target);
      if (hit) return Promise.resolve(hit);
      const started = generation;
      const result = queue.then(() => renew(target, started));
      queue = result.catch(() => undefined);
      return result;
    },
    clear: () => {
      generation += 1;
      endSession();
    },
  };
}

/** Largest token response accepted. */
const MAX_TOKEN_RESPONSE_CHARS = 64_000;
/** A hung token endpoint must not hold up the refresh queue. */
const REFRESH_TIMEOUT_MS = 10_000;

/** The refresh request of a public client (no secret), against Keycloak's token endpoint. */
export function createRefresh({
  tokenEndpoint,
  clientId,
  fetch: fetchFn = fetch,
  timeoutMs = REFRESH_TIMEOUT_MS,
}: {
  tokenEndpoint: () => Promise<string>;
  clientId: string;
  fetch?: typeof fetch;
  timeoutMs?: number;
}): Refresh {
  return async (refreshToken, scope) => {
    const endpoint = await tokenEndpoint();
    const { response, text } = await withTimeout(async (signal) => {
      const answer = await fetchFn(endpoint, {
        method: 'POST',
        headers: { 'Content-Type': 'application/x-www-form-urlencoded', Accept: 'application/json' },
        body: new URLSearchParams({
          grant_type: 'refresh_token',
          client_id: clientId,
          refresh_token: refreshToken,
          scope,
        }),
        credentials: 'omit',
        referrerPolicy: 'no-referrer',
        cache: 'no-store',
        signal,
      });
      return { response: answer, text: await answer.text() };
    }, timeoutMs);
    let body: Record<string, unknown> = {};
    try {
      const parsed: unknown = text.length <= MAX_TOKEN_RESPONSE_CHARS ? JSON.parse(text) : undefined;
      if (typeof parsed === 'object' && parsed !== null) body = parsed as Record<string, unknown>;
    } catch {
      // Not JSON (a proxy's error page); handled below as a server error.
    }

    if (!response.ok) {
      // Only the error code is kept: Keycloak's description is not shown and never logged.
      if (body.error === 'invalid_grant') throw new SessionEndedError();
      if (body.error === 'invalid_scope') throw new InvalidScopeError();
      throw new Error(`The token endpoint refused the refresh (${String(response.status)}).`);
    }

    const { access_token: accessToken, refresh_token: next, expires_in: expiresIn, scope: granted } = body;
    if (typeof accessToken !== 'string' || !accessToken || typeof expiresIn !== 'number') {
      // The refresh token was used up all the same: start over through Keycloak's session.
      throw new SessionEndedError();
    }
    return {
      accessToken,
      refreshToken: typeof next === 'string' && next ? next : undefined,
      expiresIn,
      scope: typeof granted === 'string' ? granted : '',
    };
  };
}

/** Runs a request that must finish, body included, within `timeoutMs`; running out is a network failure. */
async function withTimeout<T>(run: (signal: AbortSignal) => Promise<T>, timeoutMs: number): Promise<T> {
  const signal = AbortSignal.timeout(timeoutMs);
  try {
    return await run(signal);
  } catch (error) {
    // The refresh token is kept and the request can be tried again.
    if (signal.aborted) throw new TypeError('The token endpoint did not answer in time.', { cause: error });
    throw error;
  }
}
