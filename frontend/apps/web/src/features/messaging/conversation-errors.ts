import { formatDuration, type Locale } from '@backroom/i18n';
import { ApiError } from '@/shared/http';
import type { MessageKey } from './messages';

type Translate = (key: MessageKey, values?: Readonly<Record<string, string | number>>) => string;

/** The person cannot read the conversation (any more), or it does not exist: the server answers both the same way. */
export function isConversationGone(error: unknown): boolean {
  return error instanceof ApiError && error.code === 'messaging.conversation_not_found';
}

const TEXTS: Readonly<Record<string, MessageKey>> = {
  'messaging.channel_name_taken': 'nameTaken',
  'messaging.channel_name_invalid': 'nameInvalid',
  'messaging.channel_description_invalid': 'descriptionInvalid',
  'messaging.membership_limit': 'membershipLimit',
  'messaging.member_not_in_organization': 'memberNotInOrganization',
  'messaging.conversation_archived': 'channelArchivedError',
  'messaging.conversation_not_found': 'lostAccess',
  'messaging.not_conversation_owner': 'notAllowed',
  'permissions.forbidden': 'notAllowed',
  'messaging.owner_must_be_member': 'ownerMustBeMember',
  'messaging.member_removal_unavailable': 'notAvailableYet',
};

/** What to tell the person when creating or changing a channel, or its members, failed. */
export function conversationErrorText(error: unknown, t: Translate, locale: Locale): string {
  if (!(error instanceof ApiError)) return t('generic');
  if (error.code === 'messaging.rate_limited') {
    return error.retryAfterSeconds === undefined
      ? t('rateLimited')
      : t('rateLimitedIn', { duration: formatDuration(error.retryAfterSeconds, locale) });
  }

  const key = TEXTS[error.code];
  return key ? t(key) : t('generic');
}
