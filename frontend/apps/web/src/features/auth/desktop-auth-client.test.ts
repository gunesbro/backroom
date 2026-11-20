import { describe, expect, it, vi } from 'vitest';
import { SignInRequiredError } from './auth-client';
import { createDesktopAuthClient, type Invoke } from './desktop-auth-client';

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

    await expect(client.getAccessToken()).resolves.toBe('access');
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

    await expect(client.getAccessToken()).rejects.toThrow('signed_out');
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

    await expect(client.getAccessToken()).rejects.toThrow('network');
    expect(restart).not.toHaveBeenCalled();
  });

  it('SignOut_EndsTheShellSessionAndStartsOver', async () => {
    const { client, invoke, restart } = setup({ sign_out: () => undefined });

    await client.signOut();

    expect(invoke).toHaveBeenCalledWith('sign_out');
    expect(restart).toHaveBeenCalledOnce();
  });
});
