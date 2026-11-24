import { useId, useState, type KeyboardEvent, type SubmitEvent } from 'react';
import { Button } from '@backroom/ui';
import { useTranslate } from '@/shared/locale';
import { messages } from './messages';
import { COUNTER_FROM_REMAINING, MAX_MESSAGE_LENGTH, normalizeMessageText } from './text';

/**
 * Enter sends, Shift+Enter makes a new line. The count is of the normalized text, as the server counts it; over the
 * limit nothing is sent (longer text becomes a file attachment in Phase 2).
 */
export function Composer({
  channelName,
  onSend,
}: {
  channelName: string;
  /** Gets the normalized text. */
  onSend: (text: string) => void;
}) {
  const t = useTranslate(messages);
  const counterId = useId();
  const [value, setValue] = useState('');

  const text = normalizeMessageText(value);
  const remaining = MAX_MESSAGE_LENGTH - text.length;
  const over = remaining < 0;
  const canSend = text.length > 0 && !over;

  const submit = () => {
    if (!canSend) return;
    onSend(text);
    setValue('');
  };

  const keyDown = (event: KeyboardEvent<HTMLTextAreaElement>) => {
    // Enter that confirms an input method's composition is not a send.
    if (event.key === 'Enter' && !event.shiftKey && !event.nativeEvent.isComposing) {
      event.preventDefault();
      submit();
    }
  };

  const showCounter = remaining <= COUNTER_FROM_REMAINING;
  return (
    <form
      className="bm-composer"
      onSubmit={(event: SubmitEvent<HTMLFormElement>) => {
        event.preventDefault();
        submit();
      }}
    >
      <textarea
        className="bm-composer__input"
        aria-label={t('composerLabel', { name: channelName })}
        aria-describedby={showCounter ? counterId : undefined}
        aria-invalid={over ? true : undefined}
        rows={2}
        value={value}
        onChange={(event) => {
          setValue(event.target.value);
        }}
        onKeyDown={keyDown}
      />
      <div className="bm-composer__bar">
        {showCounter ? (
          <p
            id={counterId}
            className={over ? 'bm-counter bm-counter--over' : 'bm-counter'}
            role={over ? 'status' : undefined}
          >
            {over ? t('charactersOver', { count: -remaining }) : t('charactersLeft', { count: remaining })}
          </p>
        ) : (
          <span />
        )}
        <Button type="submit" variant="primary" disabled={!canSend}>
          {t('send')}
        </Button>
      </div>
    </form>
  );
}
