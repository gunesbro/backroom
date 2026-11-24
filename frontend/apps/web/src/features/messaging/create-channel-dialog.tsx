import { useEffect, useId, useRef, useState, type KeyboardEvent, type SubmitEvent } from 'react';
import { useMutation } from '@tanstack/react-query';
import { Alert, Button, Heading } from '@backroom/ui';
import { ApiError } from '@/shared/http';
import { useTranslate } from '@/shared/locale';
import type { Channel, MessagingApi } from './api';
import { messages } from './messages';

const MAX_NAME_CHARACTERS = 80;
/** The input never takes more than this many UTF-16 units; the check below counts characters people see. */
const MAX_NAME_UNITS = 800;

const FOCUSABLE = 'button:not([disabled]), input:not([disabled]), [href], [tabindex]:not([tabindex="-1"])';

/** True when the name is not 1 to 80 user-perceived characters (trimmed, NFC), as the server counts them. */
function nameProblem(raw: string): boolean {
  const name = raw.trim().normalize('NFC');
  if (name === '') return true;
  return (
    Array.from(new Intl.Segmenter(undefined, { granularity: 'grapheme' }).segment(name)).length >
    MAX_NAME_CHARACTERS
  );
}

/** docs/design: Dialogs, "Kanal oluştur". Phase 1 has public channels only, so there is no visibility or member picker. */
export function CreateChannelDialog({
  api,
  orgId,
  onClose,
  onCreated,
}: {
  api: MessagingApi;
  orgId: string;
  onClose: () => void;
  onCreated: (channel: Channel) => void;
}) {
  const t = useTranslate(messages);
  const titleId = useId();
  const inputId = useId();
  const errorId = useId();
  const dialogRef = useRef<HTMLDivElement>(null);
  const inputRef = useRef<HTMLInputElement>(null);
  const [name, setName] = useState('');
  const [invalid, setInvalid] = useState(false);

  useEffect(() => {
    // Focus returns to what opened the dialog.
    const opener = document.activeElement;
    inputRef.current?.focus();
    return () => {
      if (opener instanceof HTMLElement) opener.focus();
    };
  }, []);

  const mutation = useMutation({
    mutationFn: (value: string) => api.createChannel(orgId, value),
    onSuccess: onCreated,
  });

  const submit = (event: SubmitEvent<HTMLFormElement>) => {
    event.preventDefault();
    if (mutation.isPending) return;
    const problem = nameProblem(name);
    setInvalid(problem);
    if (!problem) mutation.mutate(name.trim());
  };

  const keyDown = (event: KeyboardEvent) => {
    if (event.key === 'Escape') {
      event.stopPropagation();
      onClose();
      return;
    }

    if (event.key !== 'Tab') return;
    // Keeps Tab inside the dialog.
    const items = dialogRef.current?.querySelectorAll<HTMLElement>(FOCUSABLE);
    const first = items?.[0];
    const last = items?.[items.length - 1];
    if (event.shiftKey && document.activeElement === first) {
      event.preventDefault();
      last?.focus();
    } else if (!event.shiftKey && document.activeElement === last) {
      event.preventDefault();
      first?.focus();
    }
  };

  const code = mutation.error instanceof ApiError ? mutation.error.code : undefined;
  let fieldError: string | undefined;
  if (invalid || code === 'messaging.channel_name_invalid') {
    fieldError = t('nameInvalid');
  } else if (code === 'messaging.channel_name_taken') {
    fieldError = t('nameTaken');
  }

  let alert: string | undefined;
  if (mutation.error && !fieldError) {
    alert = code === 'permissions.forbidden' ? t('createForbidden') : t('generic');
  }

  return (
    <div className="bm-backdrop">
      <div
        ref={dialogRef}
        className="bm-dialog"
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        onKeyDown={keyDown}
      >
        <div className="bm-dialog__head">
          <Heading id={titleId} level={2}>
            {t('createChannelTitle')}
          </Heading>
          <button type="button" className="bm-icon-button" aria-label={t('close')} onClick={onClose}>
            <span aria-hidden="true">×</span>
          </button>
        </div>
        <form className="ph-form" onSubmit={submit} noValidate aria-busy={mutation.isPending}>
          <div className="ph-field">
            <label className="ph-field__label" htmlFor={inputId}>
              {t('channelName')}
            </label>
            <div className="bm-name-input">
              <span aria-hidden="true">#</span>
              <input
                ref={inputRef}
                id={inputId}
                className="ph-input"
                value={name}
                maxLength={MAX_NAME_UNITS}
                autoComplete="off"
                required
                aria-invalid={fieldError ? true : undefined}
                aria-describedby={fieldError ? errorId : undefined}
                onChange={(event) => {
                  setName(event.target.value);
                  setInvalid(false);
                  mutation.reset();
                }}
              />
            </div>
            {fieldError && (
              <span id={errorId} className="ph-field__error" role="alert">
                {fieldError}
              </span>
            )}
          </div>
          {alert && <Alert>{alert}</Alert>}
          <div className="bm-dialog__actions">
            <Button onClick={onClose}>{t('cancel')}</Button>
            <Button type="submit" variant="primary" disabled={mutation.isPending}>
              {t('create')}
            </Button>
          </div>
        </form>
      </div>
    </div>
  );
}
