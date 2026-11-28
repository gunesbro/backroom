import {
  Suspense,
  lazy,
  useEffect,
  useId,
  useImperativeHandle,
  useRef,
  useState,
  type KeyboardEvent,
  type ReactNode,
  type Ref,
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

/** Stands for the name inside the translated text of the reply bar. */
const NAME_MARK = '\u0001';

/** What the composer's owner can ask of it. */
export type ComposerHandle = { focus: () => void };

/** The message being replied to, as the bar above the composer shows it. */
export type ReplyBar = { name: string; text: string };

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
  focusOnMount = false,
  onEditLast,
  reply,
  onCancelReply,
  ref,
}: {
  channelName: string;
  /** Takes keyboard focus when it appears: it replaces the control that had it (the bar that offers to join). */
  focusOnMount?: boolean;
  /** Gets the normalized text and, with the rich text editor, the blocks. */
  onSend: (text: string, blocks?: Block[]) => void;
  /** ArrowUp in the empty rich text field: edit the person's newest message (absent without message editing). */
  onEditLast?: (() => void) | undefined;
  /** The message the next one replies to: a bar above the field says so (docs/design: Main, `.rbar`). */
  reply?: ReplyBar | undefined;
  /** The close button and Escape in the field end the reply. */
  onCancelReply?: (() => void) | undefined;
  ref?: Ref<ComposerHandle>;
}) {
  const t = useTranslate(messages);
  const counterId = useId();
  const hintId = useId();
  const replyId = useId();
  const inputRef = useRef<HTMLTextAreaElement>(null);
  const richRef = useRef<RichFieldHandle>(null);
  useImperativeHandle(ref, () => ({
    focus: () => {
      if (config.richTextEnabled) richRef.current?.focus();
      else inputRef.current?.focus();
    },
  }));
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

  useEffect(() => {
    if (!focusOnMount) return;
    if (config.richTextEnabled) richRef.current?.focus();
    else inputRef.current?.focus();
  }, [focusOnMount]);

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
    } else if (event.key === 'Escape' && reply && onCancelReply) {
      event.preventDefault();
      onCancelReply();
    }
  };

  const showCounter = remaining <= COUNTER_FROM_REMAINING || tooLarge || tooManyLinks;
  // The reply bar is part of the field's description, so focusing the field says whom it replies to.
  const describedBy = [reply ? replyId : '', hintId, showCounter ? counterId : ''].filter(Boolean).join(' ');
  // The bar's words come as one text with the name in it: the name is set in bold, wherever the language puts it.
  const replyParts = reply ? t('replyingTo', { name: NAME_MARK }).split(NAME_MARK) : [];
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
      {reply && (
        <div className="bm-replybar" id={replyId}>
          <Icon name="reply" size={14} />
          <span>
            {replyParts[0]}
            <b>{reply.name}</b>
            {replyParts[1]}
          </span>
          <span className="bm-replybar__text">· {reply.text}</span>
          <button
            type="button"
            aria-label={t('cancelReply')}
            title={t('cancelReply')}
            onClick={onCancelReply}
          >
            <Icon name="close" size={14} />
          </button>
        </div>
      )}
      {rich ? (
        <Suspense fallback={<RichFieldPlaceholder />}>
          <RichField
            ref={richRef}
            label={t('composerLabel', { name: channelName })}
            placeholder={t('composerPlaceholder', { name: channelName })}
            describedBy={describedBy}
            invalid={over}
            onChange={(next: RichValue) => {
              setBlocks(next.blocks);
            }}
            onSubmit={submit}
            {...(onEditLast ? { onArrowUpWhenEmpty: onEditLast } : {})}
            {...(reply && onCancelReply ? { onCancel: onCancelReply } : {})}
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
            aria-describedby={describedBy}
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
