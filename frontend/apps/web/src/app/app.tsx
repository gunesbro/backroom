import { Suspense, useCallback, useEffect, useMemo, useState } from 'react';
import {
  MutationCache,
  QueryCache,
  QueryClient,
  QueryClientProvider,
  useQueryClient,
} from '@tanstack/react-query';
import { Brand, Card, Heading, Page, Spinner, Status } from '@backroom/ui';
import { config } from '@/config';
import {
  AuthProvider,
  createDesktopAuthClient,
  createOidcAuthClient,
  isDesktopApp,
  tauriInvoke,
  useAuth,
  type AuthClient,
} from '@/features/auth';
import { hasPendingInvitation, InvitePage, saveInvitationCode } from '@/features/invitations';
import { isLivekitSpikeEnabled, LIVEKIT_SPIKE_HASH, LivekitSpike } from '@/features/livekit-spike';
import { createMessagingApi, MessagingApiContext, type MessagingApiFactory } from '@/features/messaging';
import {
  createOrganizationsApi,
  OrganizationsApiContext,
  OrganizationsGate,
  openOrganization,
  type OrganizationsApi,
} from '@/features/organizations';
import { ApiError, type AccessTokenSource } from '@/shared/http';
import { readItem, writeItem } from '@/shared/storage';
import { LocaleProvider } from '@/shared/locale-provider';
import { useTranslate } from '@/shared/locale';
import { messages } from './messages';

function useHash() {
  const [hash, setHash] = useState(window.location.hash);

  useEffect(() => {
    const update = () => {
      setHash(window.location.hash);
    };
    window.addEventListener('hashchange', update);
    return () => {
      window.removeEventListener('hashchange', update);
    };
  }, []);

  return hash;
}

/** When a 401 last sent the person to sign in; a second one soon after is a fault, not an ended session. */
export const UNAUTHORIZED_SIGN_IN_KEY = 'ph.auth.unauthorized-sign-in-at';
const UNAUTHORIZED_SIGN_IN_PAUSE_MS = 60_000;

function createQueryClient(signIn: () => Promise<void>) {
  // A 401 with a fresh token means the session was ended on the server (signed out elsewhere, revoked). If the
  // server rejects even a new session's token (a misconfigured audience, clock skew), signing in again would
  // loop through the sign-in server forever, so the error is shown instead.
  const onError = (error: Error) => {
    if (!(error instanceof ApiError) || error.status !== 401) {
      return;
    }

    const last = Number(readItem('session', UNAUTHORIZED_SIGN_IN_KEY));
    if (Date.now() - last < UNAUTHORIZED_SIGN_IN_PAUSE_MS) {
      return;
    }

    writeItem('session', UNAUTHORIZED_SIGN_IN_KEY, String(Date.now()));
    signIn().catch(() => undefined);
  };

  return new QueryClient({
    queryCache: new QueryCache({ onError }),
    mutationCache: new MutationCache({ onError }),
    defaultOptions: {
      queries: {
        // A 4xx will not change by asking again.
        retry: (failures, error) => failures < 3 && !(error instanceof ApiError && error.status < 500),
        staleTime: 30_000,
      },
    },
  });
}

// The desktop app signs in through its shell and the system browser; the web app redirects to Keycloak.
function createAuthClient(): AuthClient {
  return isDesktopApp()
    ? createDesktopAuthClient({
        invoke: tauriInvoke,
        controlPlaneUrl: config.controlPlaneUrl,
        restart: () => {
          window.location.reload();
        },
      })
    : createOidcAuthClient(config.auth);
}

type Services = {
  createMessaging?: MessagingApiFactory | undefined;
  authClient?: AuthClient | undefined;
  createApi?: ((getAccessToken: AccessTokenSource) => OrganizationsApi) | undefined;
};

// Application shell: language, server state, sign-in, then the organization. Tests pass fake services.
export function App({ authClient, createApi, createMessaging }: Services = {}) {
  const hash = useHash();
  const [auth] = useState(() => authClient ?? createAuthClient());
  const [queryClient] = useState(() => createQueryClient(auth.signIn));

  if (isLivekitSpikeEnabled && hash === LIVEKIT_SPIKE_HASH) {
    return (
      <Suspense fallback={<p>Loading…</p>}>
        <LivekitSpike />
      </Suspense>
    );
  }

  return (
    <LocaleProvider>
      <QueryClientProvider client={queryClient}>
        <AuthProvider client={auth}>
          <SignedIn createApi={createApi} createMessaging={createMessaging} />
        </AuthProvider>
      </QueryClientProvider>
    </LocaleProvider>
  );
}

function SignedIn({ createApi, createMessaging }: Pick<Services, 'createApi' | 'createMessaging'>) {
  const { getAccessToken } = useAuth();
  const api = useMemo(
    () => (createApi ?? ((tokens) => createOrganizationsApi(config.controlPlaneUrl, tokens)))(getAccessToken),
    [createApi, getAccessToken],
  );

  // TODO(phase-1 step 7, per-cell tokens): a cell accepts only a token with its own audience (contract, "Audience per
  // target"). Until the auth clients can ask for one (scope cell:{id}), the control-plane token is sent, which a real cell
  // refuses; tests and development use fakes.
  const messagingApi = useMemo<MessagingApiFactory>(
    () => createMessaging ?? ((cell) => createMessagingApi(cell.url, getAccessToken)),
    [createMessaging, getAccessToken],
  );

  const queryClient = useQueryClient();
  // An invitation link, a pasted invitation or an acceptance being waited for comes first.
  const [inviting, setInviting] = useState(hasPendingInvitation);

  const join = useCallback((code: string) => {
    saveInvitationCode(code);
    setInviting(true);
  }, []);
  const joined = useCallback(
    (organizationId?: string) => {
      if (organizationId) openOrganization(queryClient, organizationId);
      setInviting(false);
    },
    [queryClient],
  );

  if (inviting) {
    return (
      <Suspense fallback={<Loading />}>
        <InvitePage onDone={joined} />
      </Suspense>
    );
  }

  return (
    <OrganizationsApiContext.Provider value={api}>
      <MessagingApiContext.Provider value={messagingApi}>
        <OrganizationsGate onJoin={join} />
      </MessagingApiContext.Provider>
    </OrganizationsApiContext.Provider>
  );
}

/** While a screen's code loads. */
function Loading() {
  const t = useTranslate(messages);
  return (
    <Page>
      <Card narrow>
        <Brand name={config.productName} />
        <Status>
          <Spinner />
          <Heading>{t('loading')}</Heading>
        </Status>
      </Card>
    </Page>
  );
}
