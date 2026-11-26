import {
  Suspense,
  lazy,
  useId,
  useRef,
  useState,
  type KeyboardEvent,
  type ReactNode,
  type SubmitEvent,
} from 'react';
import { Icon, SoonButton, Toolbar, type IconName } from '@backroom/ui';
import { isComposingKey } from '@/shared/keyboard';
import { useTranslate } from '@/shared/locale';
import { config } from '@/config';
import { messages } from './messages';
import {
  MAX_BLOCKS_JSON_BYTES,
  MAX_LINKS,
  blocksJsonBytes,
  blocksToText,
  distinctWebLinks,
  type Block,
} from './rich-text/blocks';
import type { RichFieldHandle, RichValue } from './rich-text/rich-field';
import { COUNTER_FROM_REMAINING, MAX_MESSAGE_LENGTH, normalizeMessageText } from './text';

type SoonKey =
  | 'bold'
  | 'italic'
  | 'strikethrough'
  | 'link'
  | 'bulletedList'
  | 'numberedList'
  | 'quote'
  | 'code'
  | 'codeBlock'
  | 'addMenu'
  | 'toggleFormatting'
  | 'emoji'
  | 'mention'
  | 'attachFile';

/** The editor and Lexical load as their own chunk, only when the rich text flag is on. */
/** Stands in for the rich text field while it loads: the toolbar row and the input at their final height, so the page does not move when the field arrives. */
export function RichFieldPlaceholder() {
  return (
    <div className="bm-rt-loading" aria-busy="true">
      <div className="bm-format" aria-hidden="true">
        <span className="bm-format__reserve" />
      </div>
      <div className="bm-composer__input bm-rt__input" />
    </div>
  );
}

const RichField = lazy(() =>
  import('./rich-text/rich-field').then(({ RichField: field }) => ({ default: field })),
);

/** The formatting toolbar in groups (docs/design: Main); formatting comes in Phase 2. */
const FORMATS: readonly (readonly { key: SoonKey; icon?: IconName; text?: string }[])[] = [
  [
    { key: 'bold', text: 'B' },
    { key: 'italic', text: 'I' },
    { key: 'strikethrough', text: 'S' },
  ],
  [
    { key: 'link', icon: 'link' },
    { key: 'bulletedList', icon: 'bulletList' },
    { key: 'numberedList', icon: 'numberedList' },
    { key: 'quote', icon: 'quote' },
  ],
  [
    { key: 'code', icon: 'code' },
    { key: 'codeBlock', icon: 'codeBlock' },
  ],
];

/**
 * docs/design: Main, the composer: the formatting toolbar, the text and a bar with the add menu, formatting, emoji,
 * mention and file buttons (all for Phase 2), the keyboard hint and the send button.
 *
 * Enter sends, Shift+Enter makes a new line. The count is of the normalized text, as the server counts it; over the
 * limit nothing is sent (longer text becomes a file attachment in Phase 2).
 */
export function Composer({
  channelName,
  onSend,
}: {
  channelName: string;
  /** Gets the normalized text and, with the rich text editor, the blocks. */
  onSend: (text: string, blocks?: Block[]) => void;
}) {
  const t = useTranslate(messages);
  const counterId = useId();
  const hintId = useId();
  const inputRef = useRef<HTMLTextAreaElement>(null);
  const richRef = useRef<RichFieldHandle>(null);
  const rich = config.richTextEnabled;
  const [value, setValue] = useState('');
  const [blocks, setBlocks] = useState<Block[]>([]);

  // The count is of the text the server derives from the blocks, so the composer's count is the server's.
  const text = rich ? blocksToText(blocks) : normalizeMessageText(value);
  const remaining = MAX_MESSAGE_LENGTH - text.length;
  const tooLarge = rich && blocksJsonBytes(blocks) > MAX_BLOCKS_JSON_BYTES;
  const links = rich ? distinctWebLinks(blocks) : 0;
  const tooManyLinks = links > MAX_LINKS;
  const over = remaining < 0 || tooLarge || tooManyLinks;
  const canSend = text.length > 0 && !over;

  const submit = () => {
    if (!canSend) return;
    if (rich) {
      onSend(text, blocks);
      richRef.current?.clear();
      setBlocks([]);
      richRef.current?.focus();
      return;
    }
    onSend(text);
    setValue('');
    // Sending with the button must not take the keyboard away from the field.
    inputRef.current?.focus();
  };

  const keyDown = (event: KeyboardEvent<HTMLTextAreaElement>) => {
    // Enter that confirms an input method's composition is not a send.
    if (event.key === 'Enter' && !event.shiftKey && !isComposingKey(event.nativeEvent)) {
      event.preventDefault();
      submit();
    }
  };

  const showCounter = remaining <= COUNTER_FROM_REMAINING || tooLarge || tooManyLinks;
  const soon = t('soon');
  const passive = (key: SoonKey, content: ReactNode, className?: string) => (
    <SoonButton key={key} label={t(key)} soon={soon} className={className}>
      {content}
    </SoonButton>
  );
  return (
    <form
      className="bm-composer"
      onSubmit={(event: SubmitEvent<HTMLFormElement>) => {
        event.preventDefault();
        submit();
      }}
    >
      {rich ? (
        <Suspense fallback={<RichFieldPlaceholder />}>
          <RichField
            ref={richRef}
            label={t('composerLabel', { name: channelName })}
            placeholder={t('composerPlaceholder', { name: channelName })}
            describedBy={showCounter ? `${hintId} ${counterId}` : hintId}
            invalid={over}
            onChange={(next: RichValue) => {
              setBlocks(next.blocks);
            }}
            onSubmit={submit}
          />
        </Suspense>
      ) : (
        <>
          <Toolbar label={t('formatting')} className="bm-format">
            {FORMATS.map((group, index) => (
              <span key={group[0]?.key} className="bm-format__group">
                {index > 0 && <span className="bm-format__sep" aria-hidden="true" />}
                {group.map((item) =>
                  passive(
                    item.key,
                    item.icon ? (
                      <Icon name={item.icon} size={16} />
                    ) : (
                      <span className={`bm-format__${item.key}`} aria-hidden="true">
                        {item.text}
                      </span>
                    ),
                  ),
                )}
              </span>
            ))}
          </Toolbar>
          <textarea
            ref={inputRef}
            className="bm-composer__input"
            aria-label={t('composerLabel', { name: channelName })}
            aria-describedby={showCounter ? `${hintId} ${counterId}` : hintId}
            aria-invalid={over ? true : undefined}
            placeholder={t('composerPlaceholder', { name: channelName })}
            rows={1}
            value={value}
            onChange={(event) => {
              setValue(event.target.value);
            }}
            onKeyDown={keyDown}
          />
        </>
      )}
      <div className="bm-composer__bar">
        <Toolbar label={t('messageTools')} className="bm-composer__tools">
          {passive('addMenu', <Icon name="plus" />, 'bm-composer__plus')}
          {passive(
            'toggleFormatting',
            <span className="bm-composer__aa" aria-hidden="true">
              Aa
            </span>,
          )}
          {passive('emoji', <Icon name="smile" />)}
          {passive('mention', <Icon name="mention" />)}
          {passive('attachFile', <Icon name="attach" />)}
        </Toolbar>
        <div className="bm-composer__meta">
          {showCounter ? (
            <p
              id={counterId}
              className={over ? 'bm-counter bm-counter--over' : 'bm-counter'}
              role={over ? 'status' : undefined}
            >
              {tooManyLinks
                ? t('linksLimit', { count: links })
                : tooLarge
                  ? t('formattingTooLarge')
                  : over
                    ? t('charactersOver', { count: -remaining })
                    : t('charactersLeft', { count: remaining })}
            </p>
          ) : null}
          <p id={hintId} className="bm-composer__hint">
            {t('composerHint')}
          </p>
        </div>
        <button
          type="submit"
          className="bm-send"
          aria-label={t('send')}
          title={t('send')}
          disabled={!canSend}
        >
          <Icon name="send" size={17} />
        </button>
      </div>
    </form>
  );
}
