import { useId, useMemo, useRef, useState, type SubmitEvent } from 'react';
import { useMutation } from '@tanstack/react-query';
import { Alert, Button, Dialog, Note, RadioCards } from '@backroom/ui';
import { ApiError } from '@/shared/http';
import { useLocale, useTranslate } from '@/shared/locale';
import type { Channel, MessagingApi, NewChannel } from './api';
import {
  descriptionProblem,
  MAX_DESCRIPTION_UNITS,
  MAX_NAME_UNITS,
  nameProblem,
  normalizeText,
  sameNameExists,
} from './channel-fields';
import { conversationErrorText } from './conversation-errors';
import { MemberPicker, type PickedMember } from './member-picker';
import { messages } from './messages';

const NOBODY: ReadonlySet<string> = new Set();

/**
 * docs/design: Dialogs, "Kanal oluştur": a name, an optional description, and whether the channel is public or
 * private. A private channel also takes the people to add (up to 50); only they and the creator, its owner, see it.
 */
export function CreateChannelDialog({
  api,
  orgId,
  existingNames,
  onClose,
  onCreated,
}: {
  api: MessagingApi;
  orgId: string;
  /** Names of the channels the person can see, to warn about a private channel with the same name. */
  existingNames: readonly string[];
  onClose: () => void;
  onCreated: (channel: Channel) => void;
}) {
  const t = useTranslate(messages);
  const locale = useLocale();
  const inputId = useId();
  const errorId = useId();
  const warningId = useId();
  const descriptionId = useId();
  const descriptionErrorId = useId();
  const visibilityName = useId();
  const inputRef = useRef<HTMLInputElement>(null);
  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [visibility, setVisibility] = useState<'public' | 'private'>('public');
  const [people, setPeople] = useState<readonly PickedMember[]>([]);
  const [invalid, setInvalid] = useState({ name: false, description: false });
  const isPrivate = visibility === 'private';

  const mutation = useMutation({
    mutationFn: (channel: NewChannel) => api.createChannel(orgId, channel),
    onSuccess: onCreated,
  });

  const submit = (event: SubmitEvent<HTMLFormElement>) => {
    event.preventDefault();
    if (mutation.isPending) return;
    const problems = { name: nameProblem(name), description: descriptionProblem(description) };
    setInvalid(problems);
    if (problems.name || problems.description) return;
    const about = normalizeText(description);
    mutation.mutate({
      name: normalizeText(name),
      ...(isPrivate ? { private: true } : {}),
      ...(about === '' ? {} : { description: about }),
      ...(isPrivate && people.length > 0
        ? { memberAccountIds: people.map((person) => person.accountId) }
        : {}),
    });
  };

  const code = mutation.error instanceof ApiError ? mutation.error.code : undefined;
  let nameError: string | undefined;
  if (invalid.name || code === 'messaging.channel_name_invalid') {
    nameError = t('nameInvalid');
  } else if (code === 'messaging.channel_name_taken') {
    nameError = t('nameTaken');
  }

  const descriptionError =
    invalid.description || code === 'messaging.channel_description_invalid'
      ? t('descriptionInvalid')
      : undefined;
  let alert: string | undefined;
  if (mutation.error && !nameError && !descriptionError) {
    alert =
      code === 'permissions.forbidden'
        ? t('createForbidden')
        : conversationErrorText(mutation.error, t, locale);
  }

  // Only for a private channel: a public name that is taken is refused by the server instead.
  const sameName = useMemo(
    () => isPrivate && sameNameExists(name, existingNames),
    [isPrivate, name, existingNames],
  );
  const nameDescribedBy = [nameError && errorId, sameName && warningId].filter(Boolean).join(' ');

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
                aria-invalid={nameError ? true : undefined}
                aria-describedby={nameDescribedBy === '' ? undefined : nameDescribedBy}
                onChange={(event) => {
                  setName(event.target.value);
                  setInvalid((current) => ({ ...current, name: false }));
                  mutation.reset();
                }}
              />
            </div>
            {nameError && (
              <span id={errorId} className="ph-field__error" role="alert">
                {nameError}
              </span>
            )}
            {sameName && (
              <span id={warningId} className="ph-field__hint" role="status">
                {t('sameNameWarning')}
              </span>
            )}
          </div>
          <div className="ph-field">
            <label className="ph-field__label" htmlFor={descriptionId}>
              {t('description')} <span className="bm-optional">{t('optional')}</span>
            </label>
            <input
              id={descriptionId}
              className="ph-input"
              value={description}
              maxLength={MAX_DESCRIPTION_UNITS}
              autoComplete="off"
              aria-invalid={descriptionError ? true : undefined}
              aria-describedby={descriptionError ? descriptionErrorId : undefined}
              onChange={(event) => {
                setDescription(event.target.value);
                setInvalid((current) => ({ ...current, description: false }));
                mutation.reset();
              }}
            />
            {descriptionError && (
              <span id={descriptionErrorId} className="ph-field__error" role="alert">
                {descriptionError}
              </span>
            )}
          </div>
          <RadioCards
            legend={t('visibility')}
            name={visibilityName}
            value={visibility}
            onChange={(value) => {
              setVisibility(value === 'private' ? 'private' : 'public');
              mutation.reset();
            }}
            options={[
              { value: 'public', label: t('visibilityPublic'), description: t('visibilityPublicDetail') },
              { value: 'private', label: t('visibilityPrivate'), description: t('visibilityPrivateDetail') },
            ]}
          />
          {isPrivate && (
            <>
              <MemberPicker
                api={api}
                orgId={orgId}
                label={t('addPeople')}
                selected={people}
                onChange={(next) => {
                  setPeople(next);
                  mutation.reset();
                }}
                excluded={NOBODY}
              />
              <Note>{t('privateNote')}</Note>
            </>
          )}
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
