import { formatDuration, type Locale } from '@backroom/i18n';
import { ApiError } from '@/shared/http';

type Translate = (key: ErrorKey, values?: Readonly<Record<string, string | number>>) => string;
export type ErrorKey =
  | 'emailInvalid'
  | 'alreadyMember'
  | 'deactivated'
  | 'alreadyInvited'
  | 'roleForbidden'
  | 'forbidden'
  | 'notFound'
  | 'rateLimited'
  | 'rateLimitedAfter'
  | 'notPending'
  | 'inviterNotAuthorized'
  | 'invitationGone'
  | 'generic';

/**
 * The text for an error of the invitation endpoints, from its stable code (never its message). The cell answers
 * `already_member` and `member_deactivated` itself; nothing here guesses whether an address has an account.
 * `values` is only for `{email}` of the line that shows it.
 */
export function invitationErrorText(
  error: unknown,
  t: Translate,
  locale: Locale,
  values: Readonly<Record<string, string | number>> = {},
): string {
  if (!(error instanceof ApiError)) return t('generic');
  switch (error.code) {
    case 'organization.invitation_email_invalid':
      return t('emailInvalid', values);
    case 'organization.already_member':
      return t('alreadyMember');
    case 'organization.member_deactivated':
      return t('deactivated');
    case 'organization.invitation_pending':
      return t('alreadyInvited');
    case 'organization.invitation_rate_limited':
      return rateLimitText(error, t, locale);
    case 'permissions.forbidden':
      return t('forbidden');
    case 'organization.not_found':
      return t('notFound');
    case 'organization.invitation_not_pending':
    case 'organization.invitation_expired':
      return t('notPending');
    case 'organization.inviter_not_authorized':
      return t('inviterNotAuthorized');
    case 'organization.invitation_not_found':
      return t('invitationGone');
    default:
      return t(error.status === 429 ? 'rateLimited' : 'generic');
  }
}

/** A 429 with its Retry-After as a wait ("1 hour", "1 saat"), or without a number if the header is missing. */
export function rateLimitText(error: ApiError, t: Translate, locale: Locale): string {
  return error.retryAfterSeconds === undefined
    ? t('rateLimited')
    : t('rateLimitedAfter', { wait: formatDuration(error.retryAfterSeconds, locale) });
}
