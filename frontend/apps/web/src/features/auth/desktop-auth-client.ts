import { SignInRequiredError, type AuthClient, type AuthUser } from './auth-client';

/** Calls a command of the desktop shell (src-tauri/src/auth); errors are the shell's error codes. */
export type Invoke = <T>(command: string, args?: Record<string, unknown>) => Promise<T>;

type Options = {
  invoke: Invoke;
  /** The control plane's origin; empty means this origin. */
  controlPlaneUrl: string;
  /** Starts the app again from its sign-in check (a page reload). */
  restart: () => void;
  fetch?: typeof fetch;
};

/** The sign-in scopes allow at most 20 shared cells with ids of at most 16 characters (contract, "Limits"). */
const MAX_SHARED_CELLS = 20;
const SHARED_CELL_ID = /^[a-z0-9][a-z0-9-]{0,15}$/;

/** The sign-in screen says the browser opened; it must not wait long for the list first. */
const SHARED_CELLS_TIMEOUT_MS = 3000;

/**
 * Sign-in of the desktop app: the Rust shell signs in through the system browser and keeps the refresh token;
 * this client only asks it for the account and for access tokens, one target at a time.
 */
export function createDesktopAuthClient({
  invoke,
  controlPlaneUrl,
  restart,
  fetch: fetchFn = fetch,
}: Options): AuthClient {
  const getAccessToken = async () => {
    try {
      return await invoke<string>('access_token', { target: 'control-plane' });
    } catch (error) {
      // The session ended (signed out elsewhere, revoked, expired): start over at the sign-in screen.
      if (error === 'signed_out') restart();
      throw new Error(`The desktop app could not get an access token (${String(error)}).`, { cause: error });
    }
  };

  // Set by Cancel before the shell has a sign-in to cancel (while the cell list loads).
  let cancelled = false;
  // Read through a function: the type checker cannot see Cancel set it during the await.
  const wasCancelled = () => cancelled;

  return {
    start: async () => {
      const user = await invoke<AuthUser | null>('auth_status');
      if (!user) throw new SignInRequiredError();
      return user;
    },
    // Called when the server rejects a fresh token: drop this device's session and sign in again. Keycloak's
    // session stays, so the person's other sessions are not signed out and the browser needs no password.
    signIn: async () => {
      await invoke('forget_session').catch(() => undefined);
      restart();
    },
    getAccessToken,
    signOut: async () => {
      await invoke('sign_out');
      restart();
    },
    browserSignIn: {
      run: async (locale) => {
        cancelled = false;
        const cells = await sharedCells(controlPlaneUrl, fetchFn);
        if (wasCancelled()) {
          // The same code the shell uses for a cancelled sign-in, a string.
          // eslint-disable-next-line @typescript-eslint/only-throw-error
          throw 'cancelled';
        }
        await invoke('sign_in', { cells, locale });
      },
      reopen: () => invoke('reopen_sign_in'),
      link: () => invoke<string | null>('sign_in_link'),
      cancel: () => {
        cancelled = true;
        return invoke('cancel_sign_in');
      },
    },
  };
}

/**
 * The ids of the shared cells, whose scopes the sign-in requests. If the list cannot be read, the sign-in
 * goes ahead without them; a cell is then authorized when it is first needed.
 */
async function sharedCells(controlPlaneUrl: string, fetchFn: typeof fetch): Promise<string[]> {
  try {
    const response = await fetchFn(`${controlPlaneUrl}/api/v1/cells/shared`, {
      headers: { Accept: 'application/json' },
      signal: AbortSignal.timeout(SHARED_CELLS_TIMEOUT_MS),
    });
    if (!response.ok) return [];
    const body = (await response.json()) as { cells?: { id?: unknown }[] };
    return (body.cells ?? [])
      .map((cell) => cell.id)
      .filter((id): id is string => typeof id === 'string' && SHARED_CELL_ID.test(id))
      .slice(0, MAX_SHARED_CELLS);
  } catch {
    return [];
  }
}

/** The shell's commands; the Tauri API loads only in the desktop app, so the web bundle does not carry it. */
export const tauriInvoke: Invoke = async <T>(command: string, args?: Record<string, unknown>) => {
  const { invoke } = await import('@tauri-apps/api/core');
  return invoke<T>(command, args);
};

/** Whether this page runs inside the desktop shell (Tauri sets this global). */
export function isDesktopApp(): boolean {
  return 'isTauri' in globalThis && Boolean((globalThis as { isTauri?: unknown }).isTauri);
}
