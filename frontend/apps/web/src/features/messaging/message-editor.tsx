import { useEffect, useId, useRef, useState } from 'react';
import { Button } from '@backroom/ui';
import { useTranslate } from '@/shared/locale';
import { messages } from './messages';
import {
  MAX_BLOCKS_JSON_BYTES,
  MAX_LINKS,
  blocksJsonBytes,
  blocksToText,
  distinctWebLinks,
  type Block,
} from './rich-text/blocks';
import { RichField, type RichFieldHandle, type RichValue } from './rich-text/rich-field';
import { COUNTER_FROM_REMAINING, MAX_MESSAGE_LENGTH } from './text';

/**
 * A message being edited, in place of its text: the rich text editor filled with the message, with Cancel and Save.
 * Enter saves, Shift+Enter makes a line, Escape cancels. Saving what has not changed just closes. It loads with the
 * editor's own chunk. `onSave` answers an error text to show here, or nothing when the change went through.
 */
export function MessageEditor({
  initial,
  onSave,
  onCancel,
  takeFocus,
}: {
  initial: readonly Block[];
  onSave: (blocks: Block[]) => Promise<string | undefined>;
  onCancel: () => void;
  /** Whether this editor was just started by the person and so takes the focus (not when it is drawn again). */
  takeFocus: () => boolean;
}) {
  const t = useTranslate(messages);
  const hintId = useId();
  const counterId = useId();
  const errorId = useId();
  const emptyId = useId();
  const richRef = useRef<RichFieldHandle>(null);
  const [blocks, setBlocks] = useState<Block[]>([...initial]);
  // The editor's own form of the message, reported once when it starts: what "unchanged" is measured against.
  const baseline = useRef<string | undefined>(undefined);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | undefined>();

  const focusIfStarted = useRef(takeFocus);
  useEffect(() => {
    if (focusIfStarted.current()) richRef.current?.focus({ atEnd: true });
  }, []);

  const text = blocksToText(blocks);
  const remaining = MAX_MESSAGE_LENGTH - text.length;
  const tooLarge = blocksJsonBytes(blocks) > MAX_BLOCKS_JSON_BYTES;
  const links = distinctWebLinks(blocks);
  const tooManyLinks = links > MAX_LINKS;
  const over = remaining < 0 || tooLarge || tooManyLinks;
  const empty = text.length === 0;
  const canSave = !empty && !over && !saving;
  const showCounter = remaining <= COUNTER_FROM_REMAINING || tooLarge || tooManyLinks;

  // The editor stays until the answer comes: closing it meanwhile would leave the answer without a place.
  const cancel = () => {
    if (!saving) onCancel();
  };

  const save = () => {
    if (!canSave) return;
    if (JSON.stringify(blocks) === baseline.current) {
      onCancel();
      return;
    }

    setSaving(true);
    setError(undefined);
    void onSave(blocks).then((failure) => {
      setSaving(false);
      setError(failure);
    });
  };

  return (
    <form
      className="bm-edit"
      onSubmit={(event) => {
        event.preventDefault();
        save();
      }}
    >
      <div className="bm-edit__field">
        <RichField
          ref={richRef}
          label={t('editMessage')}
          placeholder=""
          describedBy={[hintId, showCounter && counterId, empty && emptyId, error && errorId]
            .filter(Boolean)
            .join(' ')}
          invalid={over}
          initialBlocks={initial}
          onChange={(next: RichValue) => {
            baseline.current ??= JSON.stringify(next.blocks);
            setBlocks(next.blocks);
          }}
          onSubmit={save}
          onCancel={cancel}
        />
      </div>
      <div className="bm-edit__bar">
        <p id={hintId} className="bm-composer__hint">
          {t('editHint')}
        </p>
        {showCounter && (
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
        )}
        {empty && (
          <p id={emptyId} className="bm-counter">
            {t('editEmpty')}
          </p>
        )}
        <div className="bm-edit__buttons">
          <Button disabled={saving} onClick={cancel}>
            {t('cancel')}
          </Button>
          <Button
            type="submit"
            variant="primary"
            disabled={!canSave}
            aria-describedby={empty ? emptyId : undefined}
          >
            {t('saveEdit')}
          </Button>
        </div>
      </div>
      {error && (
        <p id={errorId} className="bm-message__error" role="alert">
          {error}
        </p>
      )}
    </form>
  );
}
