import { useEffect, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Alert, Button, Card, Heading, Muted, Spinner, Status } from '@backroom/ui';
import { ApiError } from '@/shared/http';
import { useTranslate } from '@/shared/locale';
import { useOrganizationsApi } from './api';
import { messages } from './messages';
import { SignedInPage } from './signed-in-page';

/** After this the control plane raises an operations alert; the person is told they can leave. */
export const STILL_SETTING_UP_AFTER_MS = 2 * 60 * 1000;
const POLL_MS = 2000;
const SLOW_POLL_MS = 10_000;

type Props = {
  id: string;
  onActive: () => void;
  onFailed: () => void;
  /** The organization is unknown to this account (an old id): start over. */
  onGone: () => void;
};

/** Waits for a new organization's cell to create it (docs/contracts/phase-1.md, organization creation step 5). */
export function Provisioning({ id, onActive, onFailed, onGone }: Props) {
  const t = useTranslate(messages);
  const api = useOrganizationsApi();
  const [still, setStill] = useState(false);

  useEffect(() => {
    const timer = window.setTimeout(() => {
      setStill(true);
    }, STILL_SETTING_UP_AFTER_MS);
    return () => {
      window.clearTimeout(timer);
    };
  }, []);

  const query = useQuery({
    queryKey: ['organizations', id, 'state'],
    queryFn: ({ signal }) => api.getState(id, signal),
    // Paused while the tab is hidden (React Query's default), slower once it takes long, stopped on an error
    // until the person tries again.
    refetchInterval: (current) =>
      current.state.status === 'error' || (current.state.data && current.state.data.status !== 'provisioning')
        ? false
        : still
          ? SLOW_POLL_MS
          : POLL_MS,
  });

  const status = query.data?.status;
  const gone = query.error instanceof ApiError && query.error.status === 404;
  useEffect(() => {
    if (status === 'active') onActive();
    else if (gone) onGone();
  }, [status, gone, onActive, onGone]);

  return (
    <SignedInPage>
      <Card narrow>
        {status === 'failed' ? (
          <>
            <Alert announce={false}>
              <Heading>{t('createFailed')}</Heading>
            </Alert>
            <Button variant="primary" onClick={onFailed}>
              {t('tryAgain')}
            </Button>
          </>
        ) : query.isError && !gone ? (
          <>
            <Alert announce={false}>
              <Heading>{t('generic')}</Heading>
            </Alert>
            <Button
              variant="primary"
              onClick={() => {
                void query.refetch();
              }}
            >
              {t('tryAgain')}
            </Button>
          </>
        ) : (
          <Status>
            <Spinner />
            <Heading>{t('settingUp')}</Heading>
            {still && <Muted>{t('stillSettingUp')}</Muted>}
          </Status>
        )}
      </Card>
    </SignedInPage>
  );
}
