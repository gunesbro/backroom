import type { Locale } from '@backroom/i18n';
import { ApiError } from '@/shared/http';
import { conversationErrorText } from './conversation-errors';
import type { MessageKey } from './messages';
import { isRefusedContent, refusalKey } from './send-errors';

type Translate = (key: MessageKey, values?: Readonly<Record<string, string | number>>) => string;

/** The message was deleted meanwhile (`409 messaging.message_deleted`): there is nothing left to change. */
export function isMessageDeleted(error: unknown): boolean {
  return error instanceof ApiError && error.code === 'messaging.message_deleted';
}

/** What to tell the person when editing or deleting a message failed. */
export function messageChangeErrorText(error: unknown, t: Translate, locale: Locale): string {
  if (error instanceof ApiError) {
    if (isRefusedContent(error.code)) return t(refusalKey(error.code, error.reason));
    switch (error.code) {
      case 'messaging.message_not_found':
        return t('messageGone');
      case 'messaging.not_a_member':
        return t('notAMember');
      case 'messaging.message_edit_unavailable':
        return t('notAvailableYet');
      case 'messaging.not_author':
        return t('notAllowed');
      default:
        break;
    }
  }

  return conversationErrorText(error, t, locale);
}
