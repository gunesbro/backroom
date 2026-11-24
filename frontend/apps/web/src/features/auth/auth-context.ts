import { createContext, useContext } from 'react';
import type { AuthUser } from './auth-client';

export type Auth = {
  user: AuthUser;
  /** A token for the control plane only. */
  getAccessToken: () => Promise<string>;
  /** A token for one cell only (audience `cell-{cellId}`); the control plane's token never goes to a cell. */
  getCellToken: (cellId: string) => Promise<string>;
  signOut: () => Promise<void>;
  /** Desktop only: adds a cell's scope through the browser sign-in, which the person starts, or gives up on it. */
  cellSignIn?: { start: () => void; dismiss: () => void } | undefined;
};

export const AuthContext = createContext<Auth | null>(null);

/** The signed-in account; only available under AuthProvider once sign-in has finished. */
export function useAuth(): Auth {
  const auth = useContext(AuthContext);
  if (!auth) {
    throw new Error('useAuth is used outside a signed-in AuthProvider.');
  }

  return auth;
}
