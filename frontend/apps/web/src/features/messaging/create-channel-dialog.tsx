import { useId, useRef, useState, type SubmitEvent } from 'react';
import { useMutation } from '@tanstack/react-query';
import { Alert, Button, Dialog, RadioCards } from '@backroom/ui';
import { ApiError } from '@/shared/http';
import { useTranslate } from '@/shared/locale';
import type { Channel, MessagingApi } from './api';
import { messages } from './messages';

const MAX_NAME_CHARACTERS = 80;
/** The input never takes more than this many UTF-16 units; the check below counts characters people see. */
const MAX_NAME_UNITS = 800;

/** True when the name is not 1 to 80 user-perceived characters (trimmed, NFC), as the server counts them. */
function nameProblem(raw: string): boolean {
  const name = raw.trim().normalize('NFC');
  if (name === '') return true;
  return (
    Array.from(new Intl.Segmenter(undefined, { granularity: 'grapheme' }).segment(name)).length >
    MAX_NAME_CHARACTERS
  );
}

/**
 * docs/design: Dialogs, "Kanal oluştur". Phase 1 has public channels only: the description and the private choice
 * are shown for a later phase but cannot be used, and the member picker (private channels only) is left out.
 */
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
  const inputId = useId();
  const errorId = useId();
  const descriptionId = useId();
  const descriptionSoonId = useId();
  const visibilityName = useId();
  const inputRef = useRef<HTMLInputElement>(null);
  const [name, setName] = useState('');
  const [invalid, setInvalid] = useState(false);

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
    <Dialog title={t('createChannelTitle')} closeLabel={t('close')} onClose={onClose} initialFocus={inputRef}>
      <form className="ph-dialog__form" onSubmit={submit} noValidate aria-busy={mutation.isPending}>
        <div className="ph-dialog__body">
          <div className="ph-field">
            <label className="ph-field__label" htmlFor={inputId}>
              {t('channelName')}
            </label>
            <div className="ph-prefixed">
              <span className="ph-prefixed__prefix" aria-hidden="true">
                #
              </span>
              <input
                ref={inputRef}
                id={inputId}
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
          <div className="ph-field">
            <label className="ph-field__label" htmlFor={descriptionId}>
              {t('description')} <span className="bm-optional">{t('optional')}</span>
            </label>
            {/* Phase 2: channels have no description yet; the field is shown but cannot be used. */}
            <input
              id={descriptionId}
              className="ph-input"
              readOnly
              aria-disabled="true"
              aria-describedby={descriptionSoonId}
            />
            <span id={descriptionSoonId} className="ph-field__hint">
              {t('soon')}
            </span>
          </div>
          <RadioCards
            legend={t('visibility')}
            name={visibilityName}
            value="public"
            onChange={() => undefined}
            options={[
              { value: 'public', label: t('visibilityPublic'), description: t('visibilityPublicDetail') },
              {
                value: 'private',
                label: t('visibilityPrivate'),
                description: t('visibilityPrivateDetail'),
                soon: t('soon'),
              },
            ]}
          />
          {alert && <Alert>{alert}</Alert>}
        </div>
        <div className="ph-dialog__foot">
          <Button onClick={onClose}>{t('cancel')}</Button>
          <Button type="submit" variant="primary" disabled={mutation.isPending}>
            {t('create')}
          </Button>
        </div>
      </form>
    </Dialog>
  );
}
