import { useId, useRef, useState, type SubmitEvent } from 'react';
import { Alert, Button, Dialog, RadioCards } from '@backroom/ui';
import { useTranslate } from '@/shared/locale';
import type { AccountStatus } from './api';
import { messages } from './messages';
import { useSetStatus } from './queries';
import { statusErrorText } from './status-error';
import {
  current,
  endOfToday,
  fromLocalInput,
  graphemeCount,
  isValidDndUntil,
  MAX_STATUS_TEXT,
  nextStatus,
  readCustomStatus,
  toLocalInput,
  tomorrowMorning,
  type CustomStatusProblem,
} from './status';

type DndChoice = '30' | '60' | 'tomorrow' | 'custom';

/** docs/design: Thread, "Rahatsız etme ▸ 30 dk · 1 sa · yarına kadar · özel". */
export function DndDialog({ status, onClose }: { status: AccountStatus | undefined; onClose: () => void }) {
  const t = useTranslate(messages);
  const timeId = useId();
  const errorId = useId();
  const choiceName = useId();
  const [choice, setChoice] = useState<DndChoice>('30');
  const [custom, setCustom] = useState(() => toLocalInput(new Date(Date.now() + 2 * 60 * 60_000)));
  const [invalid, setInvalid] = useState(false);
  const mutation = useSetStatus();
  const active = current(status, new Date()).dndUntil !== null;

  const save = (dndUntil: string | null) => {
    mutation.mutate(nextStatus(status, new Date(), { dndUntil }), { onSuccess: onClose });
  };

  const submit = (event: SubmitEvent<HTMLFormElement>) => {
    event.preventDefault();
    if (mutation.isPending) return;
    const now = new Date();
    const until =
      choice === '30'
        ? new Date(now.getTime() + 30 * 60_000)
        : choice === '60'
          ? new Date(now.getTime() + 60 * 60_000)
          : choice === 'tomorrow'
            ? tomorrowMorning(now)
            : fromLocalInput(custom);
    if (!until || !isValidDndUntil(until, now)) {
      setInvalid(true);
      return;
    }
    save(until.toISOString());
  };

  return (
    <Dialog title={t('dndTitle')} closeLabel={t('close')} onClose={onClose}>
      <form className="ph-dialog__form" onSubmit={submit} noValidate aria-busy={mutation.isPending}>
        <div className="ph-dialog__body">
          <p className="bp-lead">{t('dndLead')}</p>
          <RadioCards
            legend={t('dndTitle')}
            name={choiceName}
            value={choice}
            onChange={(value) => {
              setChoice(value as DndChoice);
              setInvalid(false);
            }}
            options={[
              { value: '30', label: t('dnd30') },
              { value: '60', label: t('dnd60') },
              { value: 'tomorrow', label: t('dndTomorrow') },
              { value: 'custom', label: t('dndCustom') },
            ]}
          />
          {choice === 'custom' && (
            <div className="ph-field">
              <label className="ph-field__label" htmlFor={timeId}>
                {t('dndCustomTime')}
              </label>
              <input
                id={timeId}
                className="ph-input"
                type="datetime-local"
                value={custom}
                aria-invalid={invalid ? true : undefined}
                aria-describedby={invalid ? errorId : undefined}
                onChange={(event) => {
                  setCustom(event.target.value);
                  setInvalid(false);
                }}
              />
            </div>
          )}
          {invalid && (
            <span id={errorId} className="ph-field__error" role="alert">
              {t('dndTimeInvalid')}
            </span>
          )}
          {mutation.error && <Alert>{statusErrorText(mutation.error, t)}</Alert>}
        </div>
        <div className="ph-dialog__foot">
          {active && (
            <Button
              className="bp-foot-start"
              disabled={mutation.isPending}
              onClick={() => {
                save(null);
              }}
            >
              {t('dndTurnOff')}
            </Button>
          )}
          <Button onClick={onClose}>{t('cancel')}</Button>
          <Button type="submit" variant="primary" disabled={mutation.isPending}>
            {t('dndTurnOn')}
          </Button>
        </div>
      </form>
    </Dialog>
  );
}

type Expiry = 'never' | '30' | '60' | 'today' | 'custom';

/** The custom status: one emoji, a text of at most 100 characters and when it clears. */
export function CustomStatusDialog({
  status,
  onClose,
}: {
  status: AccountStatus | undefined;
  onClose: () => void;
}) {
  const t = useTranslate(messages);
  const emojiId = useId();
  const emojiHintId = useId();
  const textId = useId();
  const countId = useId();
  const timeId = useId();
  const errorId = useId();
  const expiryName = useId();
  const textRef = useRef<HTMLInputElement>(null);
  const existing = current(status, new Date()).customStatus;
  const [emoji, setEmoji] = useState(existing?.emoji ?? '');
  const [text, setText] = useState(existing?.text ?? '');
  const [expiry, setExpiry] = useState<Expiry>(existing?.expiresAt ? 'custom' : 'never');
  const [custom, setCustom] = useState(() =>
    toLocalInput(existing?.expiresAt ? new Date(existing.expiresAt) : new Date(Date.now() + 60 * 60_000)),
  );
  const [problem, setProblem] = useState<CustomStatusProblem | undefined>();
  const mutation = useSetStatus();
  const count = graphemeCount(text.trim());

  const submit = (event: SubmitEvent<HTMLFormElement>) => {
    event.preventDefault();
    if (mutation.isPending) return;
    const now = new Date();
    const expiresAt =
      expiry === 'never'
        ? null
        : expiry === '30'
          ? new Date(now.getTime() + 30 * 60_000)
          : expiry === '60'
            ? new Date(now.getTime() + 60 * 60_000)
            : expiry === 'today'
              ? endOfToday(now)
              : (fromLocalInput(custom) ?? new Date(Number.NaN));
    const read = readCustomStatus(emoji, text, expiresAt, now);
    if ('problem' in read) {
      setProblem(read.problem);
      return;
    }
    mutation.mutate(nextStatus(status, now, { customStatus: read.status }), { onSuccess: onClose });
  };

  const fieldProblem = (kinds: CustomStatusProblem[]) =>
    problem && kinds.includes(problem) ? problem : undefined;
  const emojiProblem = fieldProblem(['emojiInvalid']);
  const textProblem = fieldProblem(['textInvalid', 'statusEmpty']);
  const expiryProblem = fieldProblem(['expiryInvalid']);
  const reset = () => {
    setProblem(undefined);
    mutation.reset();
  };

  return (
    <Dialog title={t('statusTitle')} closeLabel={t('close')} onClose={onClose} initialFocus={textRef}>
      <form className="ph-dialog__form" onSubmit={submit} noValidate aria-busy={mutation.isPending}>
        <div className="ph-dialog__body">
          <div className="bp-status-row">
            <div className="ph-field bp-emoji">
              <label className="ph-field__label" htmlFor={emojiId}>
                {t('emoji')}
              </label>
              <input
                id={emojiId}
                className="ph-input"
                value={emoji}
                maxLength={32}
                autoComplete="off"
                aria-invalid={emojiProblem ? true : undefined}
                aria-describedby={emojiProblem ? `${emojiHintId} ${errorId}-emoji` : emojiHintId}
                onChange={(event) => {
                  setEmoji(event.target.value);
                  reset();
                }}
              />
            </div>
            <div className="ph-field bp-text">
              <label className="ph-field__label" htmlFor={textId}>
                {t('statusText')}
              </label>
              <input
                ref={textRef}
                id={textId}
                className="ph-input"
                value={text}
                maxLength={1000}
                autoComplete="off"
                aria-invalid={textProblem ? true : undefined}
                aria-describedby={textProblem ? `${countId} ${errorId}-text` : countId}
                onChange={(event) => {
                  setText(event.target.value);
                  reset();
                }}
              />
            </div>
          </div>
          <div className="bp-hints">
            <span id={emojiHintId} className="ph-field__hint">
              {t('emojiHint')}
            </span>
            <span id={countId} className={count > MAX_STATUS_TEXT ? 'ph-field__error' : 'ph-field__hint'}>
              {t('statusTextCount', { count })}
            </span>
          </div>
          {emojiProblem && (
            <span id={`${errorId}-emoji`} className="ph-field__error" role="alert">
              {t(emojiProblem)}
            </span>
          )}
          {textProblem && (
            <span id={`${errorId}-text`} className="ph-field__error" role="alert">
              {t(textProblem)}
            </span>
          )}
          <RadioCards
            legend={t('clearAfter')}
            name={expiryName}
            value={expiry}
            onChange={(value) => {
              setExpiry(value as Expiry);
              reset();
            }}
            options={[
              { value: 'never', label: t('clearNever') },
              { value: '30', label: t('clear30') },
              { value: '60', label: t('clear60') },
              { value: 'today', label: t('clearToday') },
              { value: 'custom', label: t('clearCustom') },
            ]}
          />
          {expiry === 'custom' && (
            <div className="ph-field">
              <label className="ph-field__label" htmlFor={timeId}>
                {t('clearCustomTime')}
              </label>
              <input
                id={timeId}
                className="ph-input"
                type="datetime-local"
                value={custom}
                aria-invalid={expiryProblem ? true : undefined}
                aria-describedby={expiryProblem ? `${errorId}-expiry` : undefined}
                onChange={(event) => {
                  setCustom(event.target.value);
                  reset();
                }}
              />
            </div>
          )}
          {expiryProblem && (
            <span id={`${errorId}-expiry`} className="ph-field__error" role="alert">
              {t(expiryProblem)}
            </span>
          )}
          {mutation.error && <Alert>{statusErrorText(mutation.error, t)}</Alert>}
        </div>
        <div className="ph-dialog__foot">
          {existing && (
            <Button
              className="bp-foot-start"
              disabled={mutation.isPending}
              onClick={() => {
                mutation.mutate(nextStatus(status, new Date(), { customStatus: null }), {
                  onSuccess: onClose,
                });
              }}
            >
              {t('clearStatus')}
            </Button>
          )}
          <Button onClick={onClose}>{t('cancel')}</Button>
          <Button type="submit" variant="primary" disabled={mutation.isPending}>
            {t('save')}
          </Button>
        </div>
      </form>
    </Dialog>
  );
}
