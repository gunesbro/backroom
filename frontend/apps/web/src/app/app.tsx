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
  CellSignInPendingError,
  CellUnavailableError,
  createDesktopAuthClient,
  createOidcAuthClient,
  isDesktopApp,
  tauriInvoke,
  useAuth,
  type AuthClient,
} from '@/features/auth';
import { installEmojiCopy } from '@/features/emoji';
import {
  createInvitationsApiFactory,
  hasPendingInvitation,
  InvitationsApiContext,
  InvitePage,
  saveInvitationCode,
} from '@/features/invitations';
import { isLivekitSpikeEnabled, LIVEKIT_SPIKE_HASH, LivekitSpike } from '@/features/livekit-spike';
import {
  createMessagingApi,
  createRealtimeFactory,
  MessagingApiContext,
  RealtimeContext,
  type CellTokenSource,
  type RealtimeFactory,
  type MessagingApiFactory,
} from '@/features/messaging';
import {
  createOrganizationsApi,
  OrganizationsApiContext,
  OrganizationsGate,
  openOrganization,
  type OrganizationsApi,
} from '@/features/organizations';
import { createStatusApi, StatusApiContext, type StatusApi } from '@/features/presence';
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
        // A 4xx will not change by asking again, nor will a cell this sign-in cannot use.
        retry: (failures, error) =>
          failures < 3 &&
          !(error instanceof ApiError && error.status < 500) &&
          !(error instanceof CellUnavailableError || error instanceof CellSignInPendingError),
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
    : createOidcAuthClient({ ...config.auth, controlPlaneUrl: config.controlPlaneUrl });
}

type Services = {
  /** Tokens for cells; the signed-in client's per-cell tokens by default (tests pass their own). */
  cellTokens?: CellTokenSource | undefined;
  /** Overrides the messaging feature flag (tests). */
  messagingEnabled?: boolean | undefined;
  /** Replaces the live connection to cells (tests: no socket leaves jsdom). */
  realtime?: RealtimeFactory | undefined;
  /** Overrides the invitation UI feature flag (tests). */
  invitationsUiEnabled?: boolean | undefined;
  /** Overrides the presence feature flag (tests). */
  presenceEnabled?: boolean | undefined;
  /** Replaces the control plane's status API (tests). */
  createStatusApi?: ((getAccessToken: AccessTokenSource) => StatusApi) | undefined;
  authClient?: AuthClient | undefined;
  createApi?: ((getAccessToken: AccessTokenSource) => OrganizationsApi) | undefined;
};

// Application shell: language, server state, sign-in, then the organization. Tests pass fake services.
export function App({
  authClient,
  createApi,
  cellTokens,
  messagingEnabled,
  invitationsUiEnabled,
  presenceEnabled,
  createStatusApi: statusApiFactory,
  realtime,
}: Services = {}) {
  const hash = useHash();
  const [auth] = useState(() => authClient ?? createAuthClient());
  const [queryClient] = useState(() => createQueryClient(auth.signIn));
  // Emoji are images on the page; a copy takes them as characters.
  useEffect(installEmojiCopy, []);

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
          <SignedIn
            createApi={createApi}
            cellTokens={cellTokens}
            messagingEnabled={messagingEnabled}
            invitationsUiEnabled={invitationsUiEnabled}
            presenceEnabled={presenceEnabled}
            createStatusApi={statusApiFactory}
            realtimeFactory={realtime}
          />
        </AuthProvider>
      </QueryClientProvider>
    </LocaleProvider>
  );
}

function SignedIn({
  createApi,
  cellTokens,
  messagingEnabled = config.messagingEnabled,
  invitationsUiEnabled = config.invitationsUiEnabled,
  presenceEnabled = config.presenceEnabled,
  createStatusApi: statusApiFactory,
  realtimeFactory,
}: Pick<
  Services,
  | 'createApi'
  | 'cellTokens'
  | 'messagingEnabled'
  | 'invitationsUiEnabled'
  | 'presenceEnabled'
  | 'createStatusApi'
> & {
  realtimeFactory?: RealtimeFactory | undefined;
}) {
  const { getAccessToken, getCellToken } = useAuth();
  const api = useMemo(
    () => (createApi ?? ((tokens) => createOrganizationsApi(config.controlPlaneUrl, tokens)))(getAccessToken),
    [createApi, getAccessToken],
  );

  // A cell gets only a token made for it (contract, "Audience per target"), never the control plane's: the signed-in
  // client's cell token unless a test passes its own source. With the flag off there is no factory and no cell is called.
  const cellTokenSource = cellTokens ?? getCellToken;
  const messagingApi = useMemo<MessagingApiFactory | null>(
    () => (messagingEnabled ? (cell) => createMessagingApi(cell.url, () => cellTokenSource(cell.id)) : null),
    [messagingEnabled, cellTokenSource],
  );

  // Inviting needs the cell like messaging does, so it is on only when both flags are.
  const invitationsApi = useMemo(
    () => (messagingEnabled && invitationsUiEnabled ? createInvitationsApiFactory(cellTokenSource) : null),
    [messagingEnabled, invitationsUiEnabled, cellTokenSource],
  );

  // Live updates use the same cell token source as the cell API; keyed on `cellTokens` alone (the test-only prop), the
  // real app never connected.
  const realtime = useMemo(
    () => (messagingEnabled ? (realtimeFactory ?? createRealtimeFactory(cellTokenSource)) : null),
    [messagingEnabled, realtimeFactory, cellTokenSource],
  );

  // The own status lives in the control plane; with the flag off there is no API and the status menu stays passive.
  const statusApi = useMemo(
    () =>
      presenceEnabled
        ? (statusApiFactory ?? ((tokens) => createStatusApi(config.controlPlaneUrl, tokens)))(getAccessToken)
        : null,
    [presenceEnabled, statusApiFactory, getAccessToken],
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
        <RealtimeContext.Provider value={realtime}>
          <InvitationsApiContext.Provider value={invitationsApi}>
            <StatusApiContext.Provider value={statusApi}>
              <OrganizationsGate onJoin={join} />
            </StatusApiContext.Provider>
          </InvitationsApiContext.Provider>
        </RealtimeContext.Provider>
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
