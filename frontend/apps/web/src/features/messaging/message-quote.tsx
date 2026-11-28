import { Avatar } from '@backroom/ui';
import { useTranslate } from '@/shared/locale';
import type { MessageQuote } from './api';
import { messages } from './messages';

/**
 * The quote above a reply (docs/design: Main, `.quote`): a corner line, the original's author and its text on one line.
 * When the original is loaded it is a button that goes to it; otherwise (not loaded yet) it only shows the quote. A
 * deleted original shows the contract's text and nothing of its author.
 */
export function QuoteLine({
  quote,
  name,
  authorId,
  canJump,
  onJump,
}: {
  quote: MessageQuote;
  /** The original's author, as the list names people. */
  name: string;
  authorId: string;
  /** The original is in the loaded list. */
  canJump: boolean;
  /** `keyboard`: the person used the keyboard, so the focus follows to the original. */
  onJump: (messageId: string, keyboard: boolean) => void;
}) {
  const t = useTranslate(messages);
  if (quote.deleted) {
    return (
      <p className="bm-reply-quote bm-reply-quote--deleted">
        <span className="bm-reply-quote__line" aria-hidden="true" />
        <span className="bm-reply-quote__text">{t('messageDeleted')}</span>
      </p>
    );
  }

  const text = quote.text ?? '';
  const content = (
    <>
      <span className="bm-reply-quote__line" aria-hidden="true" />
      <Avatar name={name} toneKey={authorId} size="sm" />
      <b>{name}</b>
      <span className="bm-reply-quote__text">{text}</span>
    </>
  );
  if (!canJump) return <p className="bm-reply-quote">{content}</p>;
  return (
    <button
      type="button"
      className="bm-reply-quote bm-reply-quote--link"
      aria-label={t('repliedMessage', { name, text })}
      onClick={(event) => {
        // A click made by the keyboard (Enter, Space) has no pointer position.
        onJump(quote.messageId, event.detail === 0);
      }}
    >
      {content}
    </button>
  );
}
