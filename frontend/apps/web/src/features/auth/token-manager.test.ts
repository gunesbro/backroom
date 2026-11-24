import { describe, expect, it, vi } from 'vitest';
import { InvalidScopeError, ScopeNotGrantedError, SessionEndedError, targetsOf } from './cell-tokens';
import { createRefresh, createTokenManager, type Refresh } from './token-manager';

/**
 * Keycloak's token endpoint as the contract describes it: single-use refresh tokens rotated on every refresh, and a
 * refresh narrowed to one of the scopes granted at sign-in (a scope it cannot grant is silently left out).
 */
function fakeKeycloak(granted: string[]) {
  let issued = 0;
  let valid = 'refresh-0';
  let inFlight = 0;
  const log = { maxInFlight: 0, scopes: [] as string[], used: [] as string[] };
  const grantable = new Set(granted);

  const refresh = vi.fn<Refresh>(async (refreshToken, scope) => {
    inFlight += 1;
    log.maxInFlight = Math.max(log.maxInFlight, inFlight);
    log.scopes.push(scope);
    log.used.push(refreshToken);
    await new Promise((resolve) => setTimeout(resolve, 5));
    inFlight -= 1;
    if (refreshToken !== valid) throw new SessionEndedError();
    issued += 1;
    valid = `refresh-${String(issued)}`;
    return {
      accessToken: `access-${String(issued)}-for-${scope}`,
      refreshToken: valid,
      expiresIn: 300,
      scope: grantable.has(scope) ? `${scope} profile email` : 'profile email',
    };
  });
  return { refresh, log, grantable };
}

function manager(keycloak: ReturnType<typeof fakeKeycloak>, granted: string, now = () => 0) {
  return createTokenManager({
    refreshToken: 'refresh-0',
    granted: targetsOf(granted),
    refresh: keycloak.refresh,
    now,
  });
}

describe('createTokenManager', () => {
  it('Get_ConcurrentlyForDifferentTargets_RefreshesOneAtATimeWithEachRefreshTokenOnce', async () => {
    const keycloak = fakeKeycloak(['control-plane', 'cell:a', 'cell:b']);
    const tokens = manager(keycloak, 'openid control-plane cell:a cell:b');

    const answers = await Promise.all([
      tokens.get('control-plane'),
      tokens.get('cell:a'),
      tokens.get('cell:b'),
      tokens.get('cell:a'),
    ]);

    expect(answers[0]).toMatch(/-for-control-plane$/);
    expect(answers[1]).toMatch(/-for-cell:a$/);
    expect(answers[2]).toMatch(/-for-cell:b$/);
    expect(answers[3]).toBe(answers[1]);
    expect(keycloak.log.maxInFlight).toBe(1);
    expect(keycloak.log.scopes).toEqual(['control-plane', 'cell:a', 'cell:b']);
    expect(new Set(keycloak.log.used).size).toBe(keycloak.log.used.length);
  });

  it('Get_WhenTheAnswerLacksTheRequestedScope_RejectsButKeepsTheRotatedRefreshToken', async () => {
    const keycloak = fakeKeycloak(['control-plane', 'cell:a']);
    keycloak.grantable.delete('cell:a');
    const tokens = manager(keycloak, 'control-plane cell:a');

    const refused = tokens.get('cell:a');

    await expect(refused).rejects.toBeInstanceOf(ScopeNotGrantedError);
    await expect(refused).rejects.toMatchObject({ target: 'cell:a', afterRefresh: true });
    await expect(tokens.get('control-plane')).resolves.toMatch(/-for-control-plane$/);
    expect(keycloak.log.used).toEqual(['refresh-0', 'refresh-1']);
  });

  it('Get_ForATargetNotGrantedAtSignIn_RejectsWithoutAskingKeycloak', async () => {
    const keycloak = fakeKeycloak(['control-plane']);
    const tokens = manager(keycloak, 'openid control-plane');

    await expect(tokens.get('cell:other')).rejects.toMatchObject({
      target: 'cell:other',
      afterRefresh: false,
    });
    expect(keycloak.refresh).not.toHaveBeenCalled();
  });

  it('Get_UntilShortlyBeforeExpiry_UsesTheCachedTokenAndThenRefreshes', async () => {
    const keycloak = fakeKeycloak(['control-plane']);
    let time = 0;
    const tokens = manager(keycloak, 'control-plane', () => time);

    const first = await tokens.get('control-plane');
    time = 269_000;
    await expect(tokens.get('control-plane')).resolves.toBe(first);
    time = 271_000;
    const second = await tokens.get('control-plane');

    expect(second).not.toBe(first);
    expect(keycloak.refresh).toHaveBeenCalledTimes(2);
  });

  it('Get_WhenTheRefreshTokenIsRefused_EndsTheSessionWithoutAskingAgain', async () => {
    const refresh = vi.fn<Refresh>(() => Promise.reject(new SessionEndedError()));
    const tokens = createTokenManager({
      refreshToken: 'used-up',
      granted: targetsOf('control-plane'),
      refresh,
    });

    await expect(tokens.get('control-plane')).rejects.toBeInstanceOf(SessionEndedError);
    await expect(tokens.get('control-plane')).rejects.toBeInstanceOf(SessionEndedError);
    expect(refresh).toHaveBeenCalledOnce();
  });

  it('Get_AfterANetworkFailure_KeepsTheRefreshTokenForTheNextTry', async () => {
    const keycloak = fakeKeycloak(['control-plane']);
    keycloak.refresh.mockRejectedValueOnce(new TypeError('Failed to fetch'));
    const tokens = manager(keycloak, 'control-plane');

    await expect(tokens.get('control-plane')).rejects.toThrow('Failed to fetch');
    await expect(tokens.get('control-plane')).resolves.toMatch(/-for-control-plane$/);
  });

  it('Clear_WhileARefreshRuns_DropsItsAnswer', async () => {
    const keycloak = fakeKeycloak(['control-plane']);
    const tokens = manager(keycloak, 'control-plane');

    const running = tokens.get('control-plane');
    tokens.clear();

    await expect(running).rejects.toBeInstanceOf(SessionEndedError);
    await expect(tokens.get('control-plane')).rejects.toBeInstanceOf(SessionEndedError);
  });
});

describe('createRefresh', () => {
  function endpoint(response: Response) {
    const fetch = vi.fn<typeof globalThis.fetch>(() => Promise.resolve(response));
    const refresh = createRefresh({
      tokenEndpoint: () => Promise.resolve('https://id.example.test/token'),
      clientId: 'web',
      fetch,
    });
    return { fetch, refresh };
  }

  it('Refresh_NamesOneTargetInTheFormBodyAndNeverTheUrl', async () => {
    const { fetch, refresh } = endpoint(
      Response.json({ access_token: 'a', refresh_token: 'r2', expires_in: 300, scope: 'cell:eu-1' }),
    );

    await expect(refresh('r1', 'cell:eu-1')).resolves.toEqual({
      accessToken: 'a',
      refreshToken: 'r2',
      expiresIn: 300,
      scope: 'cell:eu-1',
    });
    const [url, init] = fetch.mock.calls[0] ?? [];
    expect(url).toBe('https://id.example.test/token');
    const body = new URLSearchParams(init?.body as URLSearchParams);
    expect(Object.fromEntries(body)).toEqual({
      grant_type: 'refresh_token',
      client_id: 'web',
      refresh_token: 'r1',
      scope: 'cell:eu-1',
    });
    expect(init?.credentials).toBe('omit');
  });

  it.each([
    ['invalid_grant', SessionEndedError],
    ['invalid_scope', InvalidScopeError],
  ])('Refresh_WhenKeycloakAnswers_%s_RejectsWithItsType', async (error, type) => {
    const { refresh } = endpoint(Response.json({ error, error_description: 'r1 secret' }, { status: 400 }));

    const refused = refresh('r1', 'control-plane');

    await expect(refused).rejects.toBeInstanceOf(type);
    await expect(refused).rejects.not.toThrow(/r1/);
  });

  it('Refresh_WithAnIncompleteAnswer_TreatsTheSessionAsEnded', async () => {
    const { refresh } = endpoint(Response.json({ refresh_token: 'r2' }));

    await expect(refresh('r1', 'control-plane')).rejects.toBeInstanceOf(SessionEndedError);
  });
});
