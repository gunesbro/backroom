import { afterEach, describe, expect, it, vi } from 'vitest';
import { SignInRequiredError } from './auth-client';
import { CellSignInPendingError, CellUnavailableError } from './cell-tokens';
import { createDesktopAuthClient, type Invoke } from './desktop-auth-client';

const REMEMBERED_KEY = 'ph.auth.remembered-cells';

const alice = { email: 'alice@example.test', name: 'Alice Example', givenName: 'Alice' };

function setup(
  commands: Record<string, (args?: Record<string, unknown>) => unknown>,
  cells: unknown = { cells: [] },
) {
  const invoke = vi.fn((command: string, args?: Record<string, unknown>) => {
    const handler = commands[command];
    return handler
      ? Promise.resolve().then(() => handler(args))
      : Promise.reject(new Error(`unexpected ${command}`));
  }) as unknown as Invoke & ReturnType<typeof vi.fn>;
  const restart = vi.fn();
  const fetch = vi.fn(() => Promise.resolve(new Response(JSON.stringify(cells), { status: 200 })));
  const client = createDesktopAuthClient({
    invoke,
    controlPlaneUrl: 'https://cp.example.test',
    restart,
    fetch,
  });
  return { client, invoke, restart, fetch };
}

afterEach(() => {
  localStorage.clear();
});

describe('createDesktopAuthClient', () => {
  it('Start_WithAStoredSession_ReturnsItsAccountWithoutTheNetwork', async () => {
    const { client, fetch } = setup({ auth_status: () => alice });

    await expect(client.start()).resolves.toEqual(alice);
    expect(fetch).not.toHaveBeenCalled();
  });

  it('Start_WithoutASession_AsksForTheBrowserSignIn', async () => {
    const { client } = setup({ auth_status: () => null });

    await expect(client.start()).rejects.toBeInstanceOf(SignInRequiredError);
  });

  it('BrowserSignIn_WithSharedCells_RequestsTheirScopesAndTheLanguage', async () => {
    const { client, invoke, fetch } = setup(
      { sign_in: () => undefined },
      {
        cells: [
          { id: 'eu-1' },
          { id: 'us-1' },
          { id: 'BAD ID' },
          { id: 'a-shared-id-too-long' },
          { name: 'no id' },
        ],
      },
    );

    await client.browserSignIn?.run('tr');

    expect(fetch).toHaveBeenCalledWith(
      'https://cp.example.test/api/v1/cells/shared',
      expect.objectContaining({ signal: expect.any(AbortSignal) as AbortSignal }),
    );
    expect(invoke).toHaveBeenCalledWith('sign_in', { cells: ['eu-1', 'us-1'], locale: 'tr' });
  });

  it('BrowserSignIn_WhenTheCellListFails_SignsInWithoutCells', async () => {
    const { client, invoke, fetch } = setup({ sign_in: () => undefined });
    fetch.mockRejectedValueOnce(new TypeError('offline'));

    await client.browserSignIn?.run('en');

    expect(invoke).toHaveBeenCalledWith('sign_in', { cells: [], locale: 'en' });
  });

  it('BrowserSignIn_KeepsAtMostTwentySharedCells', async () => {
    const many = { cells: Array.from({ length: 25 }, (_, index) => ({ id: `c${String(index)}` })) };
    const { client, invoke } = setup({ sign_in: () => undefined }, many);

    await client.browserSignIn?.run('en');

    expect(
      (invoke.mock.calls.find(([command]) => command === 'sign_in')?.[1] as { cells: string[] }).cells,
    ).toHaveLength(20);
  });

  it('SignIn_AfterTheServerRejectedAToken_DropsOnlyThisDevicesSession', async () => {
    const { client, invoke, restart } = setup({ forget_session: () => undefined });

    await client.signIn();

    expect(invoke).toHaveBeenCalledWith('forget_session');
    expect(invoke).not.toHaveBeenCalledWith('sign_out');
    expect(restart).toHaveBeenCalledOnce();
  });

  it('Cancel_StopsTheWaitingSignIn', async () => {
    const { client, invoke } = setup({ cancel_sign_in: () => undefined });

    await client.browserSignIn?.cancel();

    expect(invoke).toHaveBeenCalledWith('cancel_sign_in');
  });

  it('Cancel_BeforeTheBrowserOpened_NeverOpensIt', async () => {
    const { client, invoke, fetch } = setup({ cancel_sign_in: () => undefined, sign_in: () => undefined });
    let answer: (response: Response) => void = () => undefined;
    fetch.mockReturnValueOnce(
      new Promise<Response>((resolve) => {
        answer = resolve;
      }),
    );

    const run = client.browserSignIn?.run('en');
    await client.browserSignIn?.cancel();
    answer(new Response(JSON.stringify({ cells: [] }), { status: 200 }));

    await expect(run).rejects.toBe('cancelled');
    expect(invoke).not.toHaveBeenCalledWith('sign_in', expect.anything());
  });

  it('GetAccessToken_AsksTheShellForTheControlPlane', async () => {
    const { client, invoke } = setup({ access_token: () => 'access' });

    await expect(client.getAccessToken('control-plane')).resolves.toBe('access');
    expect(invoke).toHaveBeenCalledWith('access_token', { target: 'control-plane' });
  });

  it('GetAccessToken_WhenTheSessionEnded_StartsOverAtTheSignInScreen', async () => {
    const { client, restart } = setup({
      access_token: () => {
        // The shell rejects a command with its error code, a string, not an Error.
        // eslint-disable-next-line @typescript-eslint/only-throw-error
        throw 'signed_out';
      },
    });

    await expect(client.getAccessToken('control-plane')).rejects.toThrow('signed_out');
    expect(restart).toHaveBeenCalledOnce();
  });

  it('GetAccessToken_WhenOffline_KeepsTheSession', async () => {
    const { client, restart } = setup({
      access_token: () => {
        // The shell rejects a command with its error code, a string, not an Error.
        // eslint-disable-next-line @typescript-eslint/only-throw-error
        throw 'network';
      },
    });

    await expect(client.getAccessToken('control-plane')).rejects.toThrow('network');
    expect(restart).not.toHaveBeenCalled();
  });

  it('SignOut_EndsTheShellSessionAndStartsOver', async () => {
    const { client, invoke, restart } = setup({ sign_out: () => undefined });

    await client.signOut();

    expect(invoke).toHaveBeenCalledWith('sign_out');
    expect(restart).toHaveBeenCalledOnce();
  });

  it('GetAccessToken_ForACell_AsksTheShellForThatCellOnly', async () => {
    const { client, invoke } = setup({ access_token: (args) => `access-${String(args?.target)}` });

    await expect(client.getAccessToken('cell:eu-1')).resolves.toBe('access-cell:eu-1');
    expect(invoke).toHaveBeenCalledWith('access_token', { target: 'cell:eu-1' });
  });

  it('GetAccessToken_ForACellNotGranted_WaitsForThePersonAndSignsInWithTheCell', async () => {
    let granted = false;
    const { client, invoke } = setup(
      {
        access_token: () => {
          // eslint-disable-next-line @typescript-eslint/only-throw-error
          if (!granted) throw 'scope_not_granted';
          return 'access';
        },
        sign_in: () => {
          granted = true;
        },
      },
      { cells: [{ id: 'eu-1' }] },
    );

    await expect(client.getAccessToken('cell:own-1')).rejects.toBeInstanceOf(CellSignInPendingError);
    await expect(client.getAccessToken('cell:own-1')).rejects.toBeInstanceOf(CellSignInPendingError);
    expect(invoke).not.toHaveBeenCalledWith('sign_in', expect.anything());

    await client.browserSignIn?.run('en');

    expect(invoke).toHaveBeenCalledWith('sign_in', { cells: ['eu-1', 'own-1'], locale: 'en' });
    expect(Object.keys(JSON.parse(localStorage.getItem(REMEMBERED_KEY) ?? '{}') as object)).toEqual([
      'own-1',
    ]);
    await expect(client.getAccessToken('cell:own-1')).resolves.toBe('access');
  });

  it('GetAccessToken_ForACellStillMissingAfterTheBrowserSignIn_IsUnavailableWithoutAskingAgain', async () => {
    const { client } = setup({
      access_token: () => {
        // eslint-disable-next-line @typescript-eslint/only-throw-error
        throw 'scope_not_granted';
      },
      sign_in: () => undefined,
    });
    await client.getAccessToken('cell:own-1').catch(() => undefined);

    await client.browserSignIn?.run('en');

    await expect(client.getAccessToken('cell:own-1')).rejects.toBeInstanceOf(CellUnavailableError);
  });

  it('DismissCell_ForTheWaitingCell_ShowsItUnavailableWithoutTheBrowser', async () => {
    const { client, invoke } = setup({
      access_token: () => {
        // eslint-disable-next-line @typescript-eslint/only-throw-error
        throw 'scope_not_granted';
      },
    });
    await client.getAccessToken('cell:own-1').catch(() => undefined);

    client.browserSignIn?.dismissCell();

    await expect(client.getAccessToken('cell:own-1')).rejects.toBeInstanceOf(CellUnavailableError);
    expect(invoke).not.toHaveBeenCalledWith('sign_in', expect.anything());
  });

  it('BrowserSignIn_CancelledWhileAuthorizingACell_ShowsItUnavailable', async () => {
    const { client } = setup({
      access_token: () => {
        // eslint-disable-next-line @typescript-eslint/only-throw-error
        throw 'scope_not_granted';
      },
      sign_in: () => {
        // eslint-disable-next-line @typescript-eslint/only-throw-error
        throw 'cancelled';
      },
    });
    await client.getAccessToken('cell:own-1').catch(() => undefined);

    await expect(client.browserSignIn?.run('en')).rejects.toBe('cancelled');

    await expect(client.getAccessToken('cell:own-1')).rejects.toBeInstanceOf(CellUnavailableError);
  });

  it('BrowserSignIn_WhenAScopeIsUnknown_ForgetsTheRememberedCellsAndRetriesOnceWithoutThem', async () => {
    localStorage.setItem(REMEMBERED_KEY, JSON.stringify({ gone: Date.now() - 1_000 }));
    const signIn = vi
      .fn()
      .mockImplementationOnce(() => {
        // eslint-disable-next-line @typescript-eslint/only-throw-error
        throw 'invalid_scope';
      })
      .mockImplementationOnce(() => {
        // eslint-disable-next-line @typescript-eslint/only-throw-error
        throw 'invalid_scope';
      });
    const { client, invoke } = setup({ sign_in: signIn }, { cells: [{ id: 'eu-1' }] });

    await expect(client.browserSignIn?.run('en')).rejects.toBe('invalid_scope');

    expect(invoke).toHaveBeenNthCalledWith(1, 'sign_in', { cells: ['eu-1', 'gone'], locale: 'en' });
    expect(invoke).toHaveBeenNthCalledWith(2, 'sign_in', { cells: ['eu-1'], locale: 'en' });
    expect(signIn).toHaveBeenCalledTimes(2);
    expect(localStorage.getItem(REMEMBERED_KEY)).toBeNull();
  });

  it('SignOut_ForgetsTheRememberedCells', async () => {
    localStorage.setItem(REMEMBERED_KEY, JSON.stringify({ 'own-1': Date.now() - 1_000 }));
    const { client } = setup({ sign_out: () => undefined });

    await client.signOut();

    expect(localStorage.getItem(REMEMBERED_KEY)).toBeNull();
  });
});
