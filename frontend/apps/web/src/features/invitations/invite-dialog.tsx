import { useId, useRef, useState, type ClipboardEvent, type KeyboardEvent, type SubmitEvent } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { Alert, Button, Dialog } from '@backroom/ui';
import { ApiError } from '@/shared/http';
import { useLocale, useTranslate } from '@/shared/locale';
import { normalizeEmail } from './email-address';
import { invitationsKey } from './invitation-queries';
import { invitationErrorText, rateLimitText } from './invite-errors';
import type { InvitationsApi, Role } from './manage-api';
import { messages } from './manage-messages';
import { PendingInvitations } from './pending-invitations';
import './invitations.css';

/** Addresses in one submission. Each is its own request and counts against the daily attempt limit. */
export const MAX_ADDRESSES = 20;

/** Errors that belong to one address; anything else stops the whole submission. */
const PER_ADDRESS_CODES = new Set([
  'organization.invitation_email_invalid',
  'organization.already_member',
  'organization.member_deactivated',
  'organization.invitation_pending',
]);

/** `email` is what was typed (and sent: the server normalizes it); `shown` is the address the server stored. */
type Outcome = { email: string; sent: true; shown: string } | { email: string; sent: false; reason: string };
type Run = {
  outcomes: Outcome[];
  stop?: string;
  skipped: number;
  /** The limit was hit: the addresses not tried wait for it to pass. */
  rateLimited?: boolean;
  /** The Admin role was refused: the rest can still go as Member. */
  roleRefused?: boolean;
};

/**
 * docs/design: Dialogs, "davet et" (the email tab; invitation links are not part of Phase 1). Invites one address
 * per request, so each gets its own answer, and lists the pending invitations below.
 */
export function InviteDialog({
  api,
  orgId,
  organizationName,
  onClose,
}: {
  api: InvitationsApi;
  orgId: string;
  organizationName: string;
  onClose: () => void;
}) {
  const t = useTranslate(messages);
  const locale = useLocale();
  const queryClient = useQueryClient();
  const emailTabId = useId();
  const linkTabId = useId();
  const linkSoonId = useId();
  const panelId = useId();
  const inputId = useId();
  const roleId = useId();
  const hintId = useId();
  const errorId = useId();
  const inputRef = useRef<HTMLInputElement>(null);
  const [addresses, setAddresses] = useState<string[]>([]);
  const [draft, setDraft] = useState('');
  const [problem, setProblem] = useState<string | undefined>();
  const [role, setRole] = useState<Role>('member');
  const [run, setRun] = useState<Run | undefined>();

  /** Takes addresses out of `text`; returns the list and what could not be taken (kept in the field). */
  const take = (text: string, current: string[]): { next: string[]; rest: string; error?: string } => {
    const next = [...current];
    const tokens = text.split(/[\s,;]+/u).filter(Boolean);
    for (const [index, token] of tokens.entries()) {
      // The check and the comparison use the normalized form; what is kept and sent is the typed text.
      const email = normalizeEmail(token);
      const rest = tokens.slice(index).join(' ');
      if (email === undefined) return { next, rest, error: t('emailInvalid', { email: token }) };
      if (next.some((other) => normalizeEmail(other) === email)) {
        return { next, rest, error: t('emailRepeated', { email: token }) };
      }

      if (next.length >= MAX_ADDRESSES) return { next, rest, error: t('tooMany', { count: MAX_ADDRESSES }) };
      next.push(token);
    }

    return { next, rest: '' };
  };

  const commit = (text: string): string[] | undefined => {
    const { next, rest, error } = take(text, addresses);
    setAddresses(next);
    setDraft(rest);
    setProblem(error);
    return error ? undefined : next;
  };

  const mutation = useMutation({
    mutationFn: async ({ list, as }: { list: string[]; as: Role }): Promise<Run> => {
      const outcomes: Outcome[] = [];
      for (const [index, email] of list.entries()) {
        try {
          const created = await api.create(orgId, as === 'admin' ? { email, role: as } : { email });
          outcomes.push({ email, sent: true, shown: created.email });
        } catch (error) {
          if (error instanceof ApiError && PER_ADDRESS_CODES.has(error.code)) {
            outcomes.push({ email, sent: false, reason: invitationErrorText(error, t, locale, { email }) });
            continue;
          }

          // Over a limit, forbidden, a missing organization or a network problem: the rest would fail the same way.
          const skipped = list.length - index;
          if (error instanceof ApiError && error.status === 429) {
            return { outcomes, stop: rateLimitText(error, t, locale), skipped, rateLimited: true };
          }

          if (error instanceof ApiError && error.code === 'permissions.forbidden' && as === 'admin') {
            return { outcomes, stop: t('roleForbidden'), skipped, roleRefused: true };
          }

          return { outcomes, stop: invitationErrorText(error, t, locale), skipped };
        }
      }

      return { outcomes, skipped: 0 };
    },
    onSuccess: (result) => {
      setRun(result);
      const sent = new Set(result.outcomes.filter((outcome) => outcome.sent).map((outcome) => outcome.email));
      setAddresses((current) => current.filter((email) => !sent.has(email)));
    },
    // Not awaited: the form is usable again as soon as the answers are in, not when the list has reloaded.
    onSettled: () => {
      void queryClient.invalidateQueries({ queryKey: invitationsKey(orgId) });
    },
  });

  const submit = (event: SubmitEvent<HTMLFormElement>) => {
    event.preventDefault();
    if (mutation.isPending) return;
    const list = draft.trim() === '' ? addresses : commit(draft);
    if (!list) return;
    if (list.length === 0) {
      setProblem(t('noAddress'));
      return;
    }

    setRun(undefined);
    mutation.mutate({ list, as: role });
  };

  const inputKeyDown = (event: KeyboardEvent<HTMLInputElement>) => {
    if (
      (event.key === 'Enter' || event.key === ',' || event.key === ';' || event.key === ' ') &&
      draft.trim()
    ) {
      event.preventDefault();
      commit(draft);
    } else if (event.key === 'Backspace' && draft === '' && addresses.length > 0) {
      setAddresses(addresses.slice(0, -1));
    }
  };

  const paste = (event: ClipboardEvent<HTMLInputElement>) => {
    const text = event.clipboardData.getData('text');
    if (!/[\s,;]/u.test(text.trim())) return;
    event.preventDefault();
    commit(`${draft} ${text}`);
  };

  const failed = run?.outcomes.filter((outcome) => 'reason' in outcome) ?? [];
  const sent = run?.outcomes.filter((outcome) => outcome.sent) ?? [];

  const tabKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    if (
      event.key !== 'ArrowRight' &&
      event.key !== 'ArrowLeft' &&
      event.key !== 'Home' &&
      event.key !== 'End'
    )
      return;
    const tabs = Array.from(event.currentTarget.querySelectorAll<HTMLElement>('[role="tab"]'));
    const next = tabs.find((tab) => tab !== document.activeElement);
    if (!next) return;
    event.preventDefault();
    next.focus();
  };

  return (
    <Dialog
      title={t('title', { organization: organizationName })}
      closeLabel={t('close')}
      onClose={onClose}
      initialFocus={inputRef}
    >
      {/* docs/design: Dialogs. Invitation links come in a later phase: their tab is shown but cannot be opened. */}
      <div className="ph-tabs" role="tablist" aria-label={t('inviteMethods')} onKeyDown={tabKeyDown}>
        <button
          type="button"
          id={emailTabId}
          className="ph-tab"
          role="tab"
          aria-selected="true"
          aria-controls={panelId}
        >
          {t('byEmail')}
        </button>
        <button
          type="button"
          id={linkTabId}
          className="ph-tab bi-tab--soon"
          role="tab"
          aria-selected="false"
          aria-disabled="true"
          aria-describedby={linkSoonId}
          tabIndex={-1}
        >
          {t('byLink')}{' '}
          <span id={linkSoonId} className="bi-soon">
            · {t('soon')}
          </span>
        </button>
      </div>
      <div id={panelId} className="bi-panel" role="tabpanel" aria-labelledby={emailTabId}>
        <form className="ph-dialog__form" onSubmit={submit} noValidate aria-busy={mutation.isPending}>
          <div className="ph-dialog__body bi-body">
            <div className="ph-field">
              <label className="ph-field__label" htmlFor={inputId}>
                {t('emailsLabel')}
              </label>
              <div className="bi-chips">
                {addresses.map((email) => (
                  <span key={email} className="bi-chip">
                    {email}
                    <button
                      type="button"
                      aria-label={t('remove', { email })}
                      onClick={() => {
                        setAddresses(addresses.filter((other) => other !== email));
                        inputRef.current?.focus();
                      }}
                    >
                      <span aria-hidden="true">×</span>
                    </button>
                  </span>
                ))}
                <input
                  ref={inputRef}
                  id={inputId}
                  className="bi-chips__input"
                  type="text"
                  inputMode="email"
                  value={draft}
                  placeholder={t('addEmail')}
                  autoComplete="off"
                  autoCapitalize="none"
                  spellCheck={false}
                  maxLength={800}
                  aria-invalid={problem ? true : undefined}
                  aria-describedby={problem ? `${hintId} ${errorId}` : hintId}
                  onChange={(event) => {
                    setDraft(event.target.value);
                    setProblem(undefined);
                  }}
                  onKeyDown={inputKeyDown}
                  onPaste={paste}
                  onBlur={() => {
                    if (draft.trim()) commit(draft);
                  }}
                />
              </div>
              <span id={hintId} className="ph-field__hint">
                {t('emailsHint')}
              </span>
              {problem && (
                <span id={errorId} className="ph-field__error" role="alert">
                  {problem}
                </span>
              )}
            </div>
            <div className="ph-field">
              <label className="ph-field__label" htmlFor={roleId}>
                {t('role')}
              </label>
              <select
                id={roleId}
                className="ph-input"
                value={role}
                onChange={(event) => {
                  setRole(event.target.value === 'admin' ? 'admin' : 'member');
                }}
              >
                <option value="member">{t('roleMember')}</option>
                <option value="admin">{t('roleAdmin')}</option>
              </select>
            </div>
            {run && (
              <div className="bi-results" role="status">
                {sent.map((outcome) => (
                  <p key={outcome.email}>{'shown' in outcome && t('sent', { email: outcome.shown })}</p>
                ))}
                {failed.map((outcome) => (
                  <p key={outcome.email} className="bi-results__failed">
                    {'reason' in outcome && t('notSent', { email: outcome.email, reason: outcome.reason })}
                  </p>
                ))}
                {run.rateLimited && run.skipped > 0 && <p>{t('notAttempted', { count: run.skipped })}</p>}
              </div>
            )}
            {run?.stop && <Alert>{run.stop}</Alert>}
            {run?.roleRefused && (
              <Button
                disabled={mutation.isPending}
                onClick={() => {
                  setRole('member');
                  setRun(undefined);
                  mutation.mutate({ list: addresses, as: 'member' });
                }}
              >
                {t('sendAsMember')}
              </Button>
            )}
            {mutation.isError && <Alert>{t('generic')}</Alert>}
            <PendingInvitations api={api} orgId={orgId} />
          </div>
          <div className="ph-dialog__foot">
            <Button onClick={onClose}>{run && addresses.length === 0 ? t('done') : t('cancel')}</Button>
            <Button type="submit" variant="primary" disabled={mutation.isPending}>
              {mutation.isPending ? t('sending') : t('send')}
            </Button>
          </div>
        </form>
      </div>
    </Dialog>
  );
}
