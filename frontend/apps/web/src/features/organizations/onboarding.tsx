import { useId, useState, type SubmitEvent } from 'react';
import { useMutation } from '@tanstack/react-query';
import { formatDuration } from '@backroom/i18n';
import {
  Alert,
  Button,
  Card,
  Heading,
  LinkButton,
  Muted,
  Note,
  PageTitle,
  Pair,
  RadioCards,
  TextField,
} from '@backroom/ui';
import { useAuth } from '@/features/auth';
import { extractInvitationCode } from '@/features/invitations';
import { ApiError } from '@/shared/http';
import { useLocale, useTranslate } from '@/shared/locale';
import { useOrganizationsApi } from './api';
import { messages } from './messages';
import { SignedInPage } from './signed-in-page';

/** Names of the data regions we know; any other region shows its id. */
const REGION_NAMES: Readonly<Record<string, 'regionEurope'>> = { 'eu-dev': 'regionEurope' };

/** The server allows 400 code points; maxLength counts UTF-16 units, so this never cuts a name it accepts. */
const MAX_NAME_LENGTH = 800;

type Props = {
  regions: readonly string[];
  onCreated: (id: string) => void;
  /** Opens a pasted invitation; without it only setting up an organization is offered. */
  onJoin?: ((code: string) => void) | undefined;
  /** Back to the organization list, when the account already has organizations. */
  onBack?: (() => void) | undefined;
};

/**
 * The first screen without an organization (docs/design: Onboarding). The design's list of pending invitations
 * and its trial sentence wait for invitation listing and plans.
 */
export function Onboarding({ regions, onCreated, onJoin, onBack }: Props) {
  const t = useTranslate(messages);
  const { user } = useAuth();
  return (
    <SignedInPage>
      <PageTitle lead={t('welcomeLead')}>{t('welcome', { name: user.givenName })}</PageTitle>
      <Pair>
        <CreateCard regions={regions} onCreated={onCreated} />
        {onJoin && <JoinCard onJoin={onJoin} />}
      </Pair>
      {onBack && <LinkButton onClick={onBack}>{t('yourOrganizations')}</LinkButton>}
    </SignedInPage>
  );
}

function CreateCard({ regions, onCreated }: Pick<Props, 'regions' | 'onCreated'>) {
  const t = useTranslate(messages);
  const locale = useLocale();
  const api = useOrganizationsApi();
  const headingId = useId();
  const [name, setName] = useState('');
  const [region, setRegion] = useState(regions[0] ?? '');
  const [nameError, setNameError] = useState(false);

  const mutation = useMutation({
    mutationFn: (input: { name: string; region: string }) => api.create(input),
    onSuccess: (created) => {
      onCreated(created.id);
    },
  });

  const submit = (event: SubmitEvent<HTMLFormElement>) => {
    event.preventDefault();
    if (mutation.isPending) {
      return;
    }

    const trimmed = name.trim();
    setNameError(trimmed === '');
    if (trimmed !== '') {
      mutation.mutate({ name: trimmed, region });
    }
  };

  const error = mutation.error;
  const serverNameError = error instanceof ApiError && error.code === 'directory.organization_invalid';
  let alert: string | undefined;
  if (error && !serverNameError) {
    alert = errorText(error, (key, values) => t(key, values), locale);
  }

  return (
    <Card medium labelledBy={headingId}>
      <Heading id={headingId} level={2}>
        {t('setUp')}
      </Heading>
      <form className="ph-form" onSubmit={submit} noValidate aria-busy={mutation.isPending}>
        <TextField
          label={t('name')}
          error={nameError || serverNameError ? t('nameInvalid') : undefined}
          value={name}
          maxLength={MAX_NAME_LENGTH}
          autoComplete="organization"
          required
          onChange={(event) => {
            setName(event.target.value);
          }}
        />
        <RadioCards
          legend={t('region')}
          name="region"
          value={region}
          options={regions.map((id, index) => {
            const known = REGION_NAMES[id];
            return {
              value: id,
              label: known ? t(known) : id,
              description: index === 0 ? t('regionDefault') : undefined,
            };
          })}
          onChange={setRegion}
        />
        <Note>{t('regionInfo')}</Note>
        {alert && <Alert>{alert}</Alert>}
        <Button type="submit" variant="primary" disabled={mutation.isPending}>
          {t('create')}
        </Button>
      </form>
    </Card>
  );
}

function JoinCard({ onJoin }: { onJoin: (code: string) => void }) {
  const t = useTranslate(messages);
  const headingId = useId();
  const [link, setLink] = useState('');
  const [invalid, setInvalid] = useState(false);

  const submit = (event: SubmitEvent<HTMLFormElement>) => {
    event.preventDefault();
    const code = extractInvitationCode(link);
    setInvalid(code === undefined);
    if (code !== undefined) {
      onJoin(code);
    }
  };

  return (
    <Card medium labelledBy={headingId}>
      <Heading id={headingId} level={2}>
        {t('haveInvitation')}
      </Heading>
      <form className="ph-form" onSubmit={submit} noValidate>
        <Muted>{t('joinDescription')}</Muted>
        <TextField
          label={t('invitationLink')}
          error={invalid ? t('invitationInvalid') : undefined}
          value={link}
          // The contract's link form (docs/contracts/phase-1.md, "Transport").
          placeholder="https://…/invite#…"
          // Not remembered by the browser: the link is a secret.
          autoComplete="off"
          spellCheck={false}
          onChange={(event) => {
            setLink(event.target.value);
          }}
        />
        <Button type="submit">{t('join')}</Button>
      </form>
    </Card>
  );
}

type Translate = (
  key: 'emailNotVerified' | 'limit' | 'noCell' | 'generic',
  values?: Readonly<Record<string, string>>,
) => string;

function errorText(error: Error, t: Translate, locale: ReturnType<typeof useLocale>): string {
  if (!(error instanceof ApiError)) {
    return t('generic');
  }

  switch (error.code) {
    case 'directory.email_not_verified':
      return t('emailNotVerified');
    case 'directory.organization_limit':
      // The window is a day; without the header the longest wait is the honest answer.
      return t('limit', { time: formatDuration(error.retryAfterSeconds ?? 24 * 60 * 60, locale) });
    case 'directory.no_cell_available':
      return t('noCell');
    default:
      return t('generic');
  }
}
