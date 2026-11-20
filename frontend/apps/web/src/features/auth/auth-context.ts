import { createContext, useContext } from 'react';
import type { AuthUser } from './auth-client';

export type Auth = {
  user: AuthUser;
  getAccessToken: () => Promise<string>;
  signOut: () => Promise<void>;
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
