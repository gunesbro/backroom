import { useCallback, useEffect, useMemo, useState, type ReactNode } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { formatDateTime, formatDuration } from '@backroom/i18n';
import {
  AccountRow,
  Alert,
  Brand,
  Button,
  Card,
  Heading,
  LinkButton,
  Muted,
  Note,
  Page,
  Spinner,
  Status,
} from '@backroom/ui';
import { config } from '@/config';
import { useAuth } from '@/features/auth';
import { ApiError } from '@/shared/http';
import { useLocale, useTranslate } from '@/shared/locale';
import { acceptancePollDelay } from './acceptance-polling';
import { createInvitationsApi, type InvitationsApi } from './api';
import { messages } from './messages';
import {
  clearInvitationCode,
  isAccepting,
  leaveInvitePath,
  readInvitationCodeForPage,
  setAccepting,
} from './pending-invitation';

// Query keys never hold the code: one invitation per tab, and the cache is no place for a secret.
const RESOLVE_KEY = ['invitations', 'resolve'] as const;
const ACCEPTANCE_KEY = ['invitations', 'acceptance'] as const;

type Props = {
  /** The invitation is settled: open the organization the account joined, or the app. */
  onDone: (organizationId?: string) => void;
  baseUrl?: string | undefined;
  pollDelay?: ((polls: number) => number | false) | undefined;
};

type Phase = 'review' | 'joining' | 'invalid';

/**
 * Joins an organization by invitation (docs/contracts/phase-1.md, flow 3 steps 4–7; docs/design: InviteAccept).
 * Accepting is never automatic: the person presses Join.
 */
export function InvitePage({
  onDone,
  baseUrl = config.controlPlaneUrl,
  pollDelay = acceptancePollDelay,
}: Props) {
  const { getAccessToken } = useAuth();
  const api = useMemo(() => createInvitationsApi(baseUrl, getAccessToken), [baseUrl, getAccessToken]);
  const [code] = useState(readInvitationCodeForPage);
  const [phase, setPhase] = useState<Phase>(() =>
    isAccepting() ? 'joining' : code !== undefined ? 'review' : 'invalid',
  );

  const done = useCallback(
    (organizationId?: string) => {
      leaveInvitePath();
      onDone(organizationId);
    },
    [onDone],
  );
  const invalid = useCallback(() => {
    if (code !== undefined) clearInvitationCode(code);
    setPhase('invalid');
  }, [code]);
  const joining = useCallback(() => {
    setPhase('joining');
  }, []);

  if (phase === 'joining') {
    return <Joining api={api} pollDelay={pollDelay} onDone={done} />;
  }

  if (phase === 'review' && code !== undefined) {
    return <Review api={api} code={code} onJoining={joining} onInvalid={invalid} onDone={done} />;
  }

  return <Invalid onDone={done} />;
}

function Frame({ brand, children }: { brand?: string | undefined; children: ReactNode }) {
  return (
    <Page>
      <Card narrow>
        <Brand name={brand ?? config.productName} />
        {children}
      </Card>
    </Page>
  );
}

function Review({
  api,
  code,
  onJoining,
  onInvalid,
  onDone,
}: {
  api: InvitationsApi;
  code: string;
  onJoining: () => void;
  onInvalid: () => void;
  onDone: (organizationId?: string) => void;
}) {
  const t = useTranslate(messages);
  const locale = useLocale();
  const { user, signOut } = useAuth();
  const [mismatch, setMismatch] = useState(false);

  const resolved = useQuery({
    queryKey: RESOLVE_KEY,
    queryFn: ({ signal }) => api.resolve(code, signal),
    // Resolving counts against the attempt limits, so it runs once; a retry is the person's choice. A query
    // without data counts as stale whatever its staleTime, so the background refetches are turned off as well.
    staleTime: 'static',
    gcTime: 0,
    retry: false,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
  });

  const accept = useMutation({
    mutationFn: () => api.accept(code),
    onSuccess: (accepted) => {
      clearInvitationCode(code);
      if (accepted.status === 'used') {
        setAccepting(false);
        onDone(accepted.organizationId);
      } else {
        setAccepting(true);
        onJoining();
      }
    },
    onError: (error) => {
      if (isCode(error, 'directory.invitation_invalid')) onInvalid();
      else if (isCode(error, 'directory.invitation_email_mismatch')) setMismatch(true);
    },
  });

  const unknown = isCode(resolved.error, 'directory.invitation_invalid');
  useEffect(() => {
    if (unknown) onInvalid();
  }, [unknown, onInvalid]);

  if (resolved.isPending || unknown) {
    return (
      <Frame>
        <Status>
          <Spinner />
          <Heading>{t('checking')}</Heading>
        </Status>
      </Frame>
    );
  }

  if (resolved.isError) {
    const limited = rateLimit(resolved.error);
    return (
      <Frame>
        {limited ? (
          <>
            <Heading>{t('rateLimitedTitle')}</Heading>
            <Muted>{t('rateLimitedBody', { time: formatDuration(limited, locale) })}</Muted>
          </>
        ) : (
          <Alert announce={false}>
            <Heading>{t('generic')}</Heading>
          </Alert>
        )}
        <Button
          variant="primary"
          onClick={() => {
            void resolved.refetch();
          }}
        >
          {t('tryAgain')}
        </Button>
      </Frame>
    );
  }

  const invitation = resolved.data;
  if (!invitation.matchesAccount || mismatch) {
    // Never an address: the invited one is not known here, and the signed-in one is not the point. Keyed, so a
    // mismatch found on Join is a new screen whose heading takes focus.
    return (
      <Frame key="mismatch" brand={invitation.organizationName}>
        <Heading>{t('mismatchTitle')}</Heading>
        <Muted>{t('mismatchBody')}</Muted>
        <Button
          variant="primary"
          onClick={() => {
            // The code stays kept (for its 30 minutes), so the invitation opens again after signing in with the right account.
            void signOut();
          }}
        >
          {t('signOutAndSwitch')}
        </Button>
        <Button
          onClick={() => {
            clearInvitationCode(code);
            onDone();
          }}
        >
          {t('close')}
        </Button>
      </Frame>
    );
  }

  const acceptError = accept.error;
  let alert: string | undefined;
  if (acceptError && !isCode(acceptError, 'directory.invitation_invalid')) {
    const limited = rateLimit(acceptError);
    alert = isCode(acceptError, 'directory.email_not_verified')
      ? t('emailNotVerified')
      : limited
        ? `${t('rateLimitedTitle')}. ${t('rateLimitedBody', { time: formatDuration(limited, locale) })}`
        : t('generic');
  }

  return (
    <Frame brand={invitation.organizationName}>
      <Heading>{t('joinTitle', { organization: invitation.organizationName })}</Heading>
      <Muted>{t('validUntil', { date: formatDateTime(new Date(invitation.expiresAt), locale) })}</Muted>
      <AccountRow name={user.name} detail={t('joinAs', { email: user.email })} />
      {alert && <Alert>{alert}</Alert>}
      <Button
        variant="primary"
        disabled={accept.isPending}
        aria-busy={accept.isPending}
        onClick={() => {
          accept.mutate();
        }}
      >
        {t('join')}
      </Button>
      <LinkButton
        onClick={() => {
          void signOut();
        }}
      >
        {t('otherAccount')}
      </LinkButton>
    </Frame>
  );
}

function Joining({
  api,
  pollDelay,
  onDone,
}: {
  api: InvitationsApi;
  pollDelay: (polls: number) => number | false;
  onDone: (organizationId?: string) => void;
}) {
  const t = useTranslate(messages);
  const queryClient = useQueryClient();
  const query = useQuery({
    queryKey: ACCEPTANCE_KEY,
    queryFn: ({ signal }) => api.getAcceptance(signal),
    staleTime: 0,
    gcTime: 0,
    // Slower and slower, stopped once settled, on an error or after the last poll; paused while the tab is hidden.
    refetchInterval: (current) =>
      current.state.status === 'error' || (current.state.data && current.state.data.status !== 'accepting')
        ? false
        : pollDelay(current.state.dataUpdateCount),
    refetchIntervalInBackground: false,
  });

  // Reading dataUpdatedAt re-renders on every answer, also when the answer is unchanged.
  const polls =
    query.dataUpdatedAt > 0 ? (queryClient.getQueryState(ACCEPTANCE_KEY)?.dataUpdateCount ?? 0) : 0;
  const acceptance = query.data;
  const none = isCode(query.error, 'directory.invitation_acceptance_not_found');
  const settled = none || (acceptance !== undefined && acceptance.status !== 'accepting');

  useEffect(() => {
    if (!settled) return;
    setAccepting(false);
    if (acceptance?.status === 'used') onDone(acceptance.organizationId);
    // The reason is shown once; a reload opens the app rather than an invitation that no longer exists.
    else leaveInvitePath();
  }, [settled, acceptance, onDone]);

  if (none || acceptance?.status === 'failed') {
    return (
      <Frame>
        <Alert announce={false}>
          <Heading>{t('failedTitle')}</Heading>
        </Alert>
        <Muted>{t(failureText(acceptance?.code ?? null))}</Muted>
        <Note>{t('askForNewLink')}</Note>
        <Button
          variant="primary"
          onClick={() => {
            onDone();
          }}
        >
          {t('continue')}
        </Button>
      </Frame>
    );
  }

  if (query.isError) {
    return (
      <Frame>
        <Alert announce={false}>
          <Heading>{t('generic')}</Heading>
        </Alert>
        <Button
          variant="primary"
          onClick={() => {
            void query.refetch();
          }}
        >
          {t('tryAgain')}
        </Button>
      </Frame>
    );
  }

  const still = acceptance?.status === 'accepting' && pollDelay(polls) === false;
  return (
    <Frame>
      <Status>
        <Spinner />
        <Heading>{t('joining')}</Heading>
        {still && <Muted>{t('stillJoining')}</Muted>}
      </Status>
      {still && (
        <Button
          disabled={query.isFetching}
          onClick={() => {
            void query.refetch();
          }}
        >
          {t('checkAgain')}
        </Button>
      )}
      <LinkButton
        onClick={() => {
          // Stops waiting here; the organization still appears in the list once the cell has added the person.
          setAccepting(false);
          onDone();
        }}
      >
        {t('continue')}
      </LinkButton>
    </Frame>
  );
}

function Invalid({ onDone }: { onDone: () => void }) {
  const t = useTranslate(messages);
  return (
    <Frame>
      <Heading>{t('invalidTitle')}</Heading>
      <Muted>{t('invalidBody')}</Muted>
      <Note>{t('askForNewLink')}</Note>
      <Button
        onClick={() => {
          onDone();
        }}
      >
        {t('continue')}
      </Button>
    </Frame>
  );
}

/** Why an acceptance failed, from the cell's or the link's code (docs/contracts/phase-1.md, flow 3 step 6). */
function failureText(code: string | null) {
  switch (code) {
    case 'organization.invitation_not_pending':
      return 'failedNotPending';
    case 'organization.invitation_expired':
      return 'failedExpired';
    case 'organization.invitation_email_mismatch':
      return 'failedEmailMismatch';
    case 'organization.inviter_not_authorized':
      return 'failedInviterNotAuthorized';
    case 'organization.member_deactivated':
      return 'failedMemberDeactivated';
    default:
      return 'failedOther';
  }
}

function isCode(error: unknown, code: string): boolean {
  return error instanceof ApiError && error.code === code;
}

/** Seconds to wait after a 429; without Retry-After, the attempt window of a minute. */
function rateLimit(error: unknown): number | undefined {
  return isCode(error, 'directory.invitation_rate_limited') && error instanceof ApiError
    ? (error.retryAfterSeconds ?? 60)
    : undefined;
}
