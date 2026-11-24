import { SignInRequiredError, type AuthClient, type AuthUser } from './auth-client';
import {
  CellSignInPendingError,
  CellUnavailableError,
  cellOf,
  fetchSharedCells,
  signInCells,
  type TokenTarget,
} from './cell-tokens';
import { createRememberedCells } from './remembered-cells';

/** Calls a command of the desktop shell (src-tauri/src/auth); errors are the shell's error codes. */
export type Invoke = <T>(command: string, args?: Record<string, unknown>) => Promise<T>;

type Options = {
  invoke: Invoke;
  /** The control plane's origin; empty means this origin. */
  controlPlaneUrl: string;
  /** Starts the app again from its sign-in check (a page reload). */
  restart: () => void;
  fetch?: typeof fetch;
  now?: () => number;
};

/**
 * Sign-in of the desktop app: the Rust shell signs in through the system browser, keeps the refresh token and runs
 * every refresh one at a time; this client only asks it for the account and for access tokens, one target at a time.
 */
export function createDesktopAuthClient({
  invoke,
  controlPlaneUrl,
  restart,
  fetch: fetchFn = (input, init) => fetch(input, init),
  now = Date.now,
}: Options): AuthClient {
  const remembered = createRememberedCells(now);
  // The cell waiting for the browser sign-in, and the cells already authorized once since the app started; a cell
  // still missing after that is shown as unavailable until the next sign-in, so it never loops.
  let pending: string | undefined;
  const attempted = new Set<string>();
  const listeners = new Set<() => void>();

  const getAccessToken = async (target: TokenTarget) => {
    const cellId = cellOf(target);
    try {
      const token = await invoke<string>('access_token', { target });
      if (cellId !== undefined) remembered.touch(cellId);
      return token;
    } catch (error) {
      // The session ended (signed out elsewhere, revoked, expired): start over at the sign-in screen.
      if (error === 'signed_out') restart();
      if (cellId !== undefined && error === 'scope_not_granted') {
        if (attempted.has(cellId)) throw new CellUnavailableError(cellId);
        if (pending === undefined) {
          pending = cellId;
          for (const listener of listeners) listener();
        }
        throw new CellSignInPendingError(cellId);
      }
      // The cell's scope no longer exists at Keycloak.
      if (cellId !== undefined && error === 'invalid_scope') throw new CellUnavailableError(cellId);
      throw new Error(`The desktop app could not get an access token (${String(error)}).`, { cause: error });
    }
  };

  // Set by Cancel before the shell has a sign-in to cancel (while the cell list loads).
  let cancelled = false;
  // Read through a function: the type checker cannot see Cancel set it during the await.
  const wasCancelled = () => cancelled;

  const signInWith = async (cells: string[], locale: string) => {
    if (wasCancelled()) {
      // The same code the shell uses for a cancelled sign-in, a string.
      // eslint-disable-next-line @typescript-eslint/only-throw-error
      throw 'cancelled';
    }
    await invoke('sign_in', { cells, locale });
  };

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
      remembered.forget();
      await invoke('sign_out');
      restart();
    },
    browserSignIn: {
      run: async (locale) => {
        cancelled = false;
        const cell = pending;
        try {
          const shared = await fetchSharedCells(controlPlaneUrl, fetchFn);
          if (cell !== undefined && !shared.includes(cell)) remembered.remember(cell);
          try {
            await signInWith(signInCells(shared, remembered.list(), cell), locale);
          } catch (error) {
            // A remembered cell no longer exists: once more without the remembered cells, and forget them. The cell
            // being authorized is left out too, as Keycloak does not say which scope it refused.
            if (error !== 'invalid_scope') throw error;
            remembered.forget();
            await signInWith(shared, locale);
          }
        } finally {
          if (cell !== undefined) attempted.add(cell);
          if (pending === cell) pending = undefined;
        }
      },
      reopen: () => invoke('reopen_sign_in'),
      link: () => invoke<string | null>('sign_in_link'),
      cancel: () => {
        cancelled = true;
        return invoke('cancel_sign_in');
      },
      onCellSignIn: (listener) => {
        listeners.add(listener);
        return () => {
          listeners.delete(listener);
        };
      },
    },
  };
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
