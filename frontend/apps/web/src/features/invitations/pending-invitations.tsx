import { useMemo, useRef, useState } from 'react';
import { useInfiniteQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { formatDateTime } from '@backroom/i18n';
import { Alert, Button, LinkButton, Spinner, Status } from '@backroom/ui';
import { useLocale, useTranslate } from '@/shared/locale';
import type { Invitation, InvitationsApi } from './manage-api';
import { invitationsKey } from './invitation-queries';
import { invitationErrorText } from './invite-errors';
import { messages } from './manage-messages';

/** The organization's pending invitations (all of them for a manager, otherwise the caller's own), newest page last. */
export function PendingInvitations({ api, orgId }: { api: InvitationsApi; orgId: string }) {
  const t = useTranslate(messages);
  const query = useInfiniteQuery({
    queryKey: invitationsKey(orgId),
    queryFn: ({ pageParam, signal }) => api.list(orgId, pageParam, signal),
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (last) => last.next ?? undefined,
  });
  const invitations = useMemo(
    () => query.data?.pages.flatMap((page) => page.invitations) ?? [],
    [query.data],
  );
  const { refetch, fetchNextPage } = query;

  return (
    <section className="bi-pending" aria-labelledby="bi-pending-title">
      <h3 id="bi-pending-title" className="bi-pending__title">
        {t('pendingTitle')}
      </h3>
      {query.isPending && (
        <Status>
          <Spinner />
          {t('loading')}
        </Status>
      )}
      {query.isError && !query.data && (
        <div className="bi-stack">
          <Alert>{t('pendingFailed')}</Alert>
          <Button
            onClick={() => {
              void refetch();
            }}
          >
            {t('tryAgain')}
          </Button>
        </div>
      )}
      {query.data && invitations.length === 0 && <p className="ph-muted">{t('pendingNone')}</p>}
      <ul className="bi-list">
        {invitations.map((invitation) => (
          <InvitationRow key={invitation.id} api={api} orgId={orgId} invitation={invitation} />
        ))}
      </ul>
      {query.hasNextPage && (
        <LinkButton
          disabled={query.isFetchingNextPage}
          onClick={() => {
            void fetchNextPage();
          }}
        >
          {t('showMore')}
        </LinkButton>
      )}
    </section>
  );
}

function InvitationRow({
  api,
  orgId,
  invitation,
}: {
  api: InvitationsApi;
  orgId: string;
  invitation: Invitation;
}) {
  const t = useTranslate(messages);
  const locale = useLocale();
  const queryClient = useQueryClient();
  const [confirming, setConfirming] = useState(false);
  const revokeRef = useRef<HTMLButtonElement>(null);
  const keepRef = useRef<HTMLButtonElement>(null);
  const [note, setNote] = useState<string | undefined>();
  const refresh = () => {
    void queryClient.invalidateQueries({ queryKey: invitationsKey(orgId) });
  };
  const revoke = useMutation({
    mutationFn: () => api.revoke(orgId, invitation.id),
    onSuccess: () => {
      setConfirming(false);
      setNote(t('revoked'));
    },
    onSettled: refresh,
  });
  const resend = useMutation({
    mutationFn: () => api.resend(orgId, invitation.id),
    onSuccess: () => {
      setNote(t('resent'));
    },
    onSettled: refresh,
  });
  const busy = revoke.isPending || resend.isPending;
  const error = revoke.error ?? resend.error;
  const expiresAt = new Date(invitation.expiresAt);
  const date = formatDateTime(expiresAt, locale);
  // The cell expires a pending invitation only later; until then it can still be revoked, but not resent.
  const [now] = useState(() => Date.now());
  const expired = expiresAt.getTime() <= now;

  return (
    <li className="bi-row">
      <div className="bi-row__text">
        <span className="bi-row__email">{invitation.email}</span>
        <span className="ph-muted">
          {invitation.role === 'admin' ? t('roleAdmin') : t('roleMember')} ·{' '}
          {t(expired ? 'expired' : 'expires', { date })}
        </span>
        {confirming && !revoke.isSuccess && (
          <span className="ph-muted">{t('confirmRevoke', { email: invitation.email })}</span>
        )}
        {error && (
          <span role="alert" className="bi-row__error">
            {invitationErrorText(error, t, locale)}
          </span>
        )}
        {note && !error && (
          <span role="status" className="ph-muted">
            {note}
          </span>
        )}
      </div>
      <div className="bi-row__actions">
        {confirming ? (
          <>
            <LinkButton
              disabled={busy}
              onClick={() => {
                setNote(undefined);
                resend.reset();
                revoke.mutate();
              }}
              aria-label={`${t('revoke')}: ${invitation.email}`}
            >
              {t('revoke')}
            </LinkButton>
            <button
              type="button"
              className="ph-link"
              ref={keepRef}
              onClick={() => {
                setConfirming(false);
                revokeRef.current?.focus();
              }}
            >
              {t('keep')}
            </button>
          </>
        ) : (
          <>
            <button
              type="button"
              className="ph-link"
              ref={revokeRef}
              disabled={busy}
              onClick={() => {
                setConfirming(true);
                // The confirmation replaces this button; keep focus on the safe choice.
                setTimeout(() => keepRef.current?.focus(), 0);
              }}
              aria-label={`${t('revoke')}: ${invitation.email}`}
            >
              {t('revoke')}
            </button>
            <LinkButton
              disabled={busy || expired}
              onClick={() => {
                setNote(undefined);
                revoke.reset();
                resend.mutate();
              }}
              aria-label={`${t('resend')}: ${invitation.email}`}
            >
              {t('resend')}
            </LinkButton>
          </>
        )}
      </div>
    </li>
  );
}
