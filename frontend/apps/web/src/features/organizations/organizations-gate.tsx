import { Suspense, useCallback, useMemo, useState, type ReactNode } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Alert, Button, Card, Heading, LinkButton, Spinner, Status } from '@backroom/ui';
import { config } from '@/config';
import { InviteDialog, useInvitationsApiFactory, useInvitationsEnabled } from '@/features/invitations';
import { useMessagingEnabled, Workspace } from '@/features/messaging';
import { readItem, writeItem } from '@/shared/storage';
import { useTranslate } from '@/shared/locale';
import { useOrganizationsApi, type AccountOrganization } from './api';
import { messages } from './messages';
import { LAST_ORGANIZATION_KEY, MY_ORGANIZATIONS, PENDING_ORGANIZATION_KEY } from './organization-cache';
import { Onboarding } from './onboarding';
import { OrganizationList } from './organization-list';
import { Provisioning } from './provisioning';
import { SignedInPage } from './signed-in-page';

type View = 'auto' | 'list' | 'create';

/**
 * After sign-in: no organization → set one up (or join); one → open it; several → choose, remembering the last one;
 * a new organization is waited for until its cell has created it.
 */
export function OrganizationsGate({
  regions = config.regions,
  onJoin,
}: {
  regions?: readonly string[];
  /** An invitation link or code pasted on the onboarding screen. */
  onJoin?: ((code: string) => void) | undefined;
}) {
  const t = useTranslate(messages);
  const api = useOrganizationsApi();
  const queryClient = useQueryClient();
  const [view, setView] = useState<View>('auto');
  const [pendingId, setPendingId] = useState(() => readItem('session', PENDING_ORGANIZATION_KEY));
  const [chosenId, setChosenId] = useState(() => readItem('local', LAST_ORGANIZATION_KEY));

  const organizations = useQuery({
    queryKey: MY_ORGANIZATIONS,
    queryFn: ({ signal }) => api.listMine(signal),
    enabled: pendingId === undefined,
  });

  const choose = useCallback((id: string | undefined) => {
    writeItem('local', LAST_ORGANIZATION_KEY, id);
    setChosenId(id);
    setView('auto');
  }, []);

  const settle = useCallback(
    (next: { open?: string; view: View }) => {
      // Dropped, not just invalidated: the old list (without the new organization) must not be drawn meanwhile.
      queryClient.removeQueries({ queryKey: MY_ORGANIZATIONS });
      writeItem('session', PENDING_ORGANIZATION_KEY, undefined);
      setPendingId(undefined);
      if (next.open) choose(next.open);
      setView(next.view);
    },
    [choose, queryClient],
  );

  const onActive = useCallback(() => {
    if (pendingId) settle({ open: pendingId, view: 'auto' });
  }, [pendingId, settle]);
  const onFailed = useCallback(() => {
    settle({ view: 'create' });
  }, [settle]);
  const onGone = useCallback(() => {
    settle({ view: 'auto' });
  }, [settle]);

  if (pendingId) {
    return <Provisioning id={pendingId} onActive={onActive} onFailed={onFailed} onGone={onGone} />;
  }

  if (organizations.isError && !organizations.data) {
    return (
      <Shell>
        <Alert announce={false}>
          <Heading>{t('generic')}</Heading>
        </Alert>
        <Button
          variant="primary"
          onClick={() => {
            void organizations.refetch();
          }}
        >
          {t('tryAgain')}
        </Button>
      </Shell>
    );
  }

  if (!organizations.data) {
    return (
      <Shell>
        <Status>
          <Spinner />
          <Heading>{t('loading')}</Heading>
        </Status>
      </Shell>
    );
  }

  const list = organizations.data;
  if (view === 'create' || list.length === 0) {
    return (
      <Onboarding
        regions={regions}
        onJoin={onJoin}
        onCreated={(id) => {
          writeItem('session', PENDING_ORGANIZATION_KEY, id);
          setPendingId(id);
        }}
        onBack={
          list.length > 0
            ? () => {
                setView('list');
              }
            : undefined
        }
      />
    );
  }

  const chosen =
    view === 'auto'
      ? (list.find(({ id }) => id === chosenId) ?? (list.length === 1 ? list[0] : undefined))
      : undefined;
  if (chosen) {
    return (
      <OrganizationHome
        organization={chosen}
        onSwitch={() => {
          setView('list');
        }}
      />
    );
  }

  return (
    <OrganizationList
      organizations={list}
      onChoose={choose}
      onCreate={() => {
        setView('create');
      }}
    />
  );
}

/** The invite dialog of an organization, over its workspace. Rendered only with the feature flag on. */
function OrganizationInvite({
  organization,
  onClose,
}: {
  organization: AccountOrganization;
  onClose: () => void;
}) {
  const factory = useInvitationsApiFactory();
  const api = useMemo(
    () => factory({ id: organization.cellId, url: organization.cellUrl }),
    [factory, organization.cellId, organization.cellUrl],
  );
  return (
    <InviteDialog api={api} orgId={organization.id} organizationName={organization.name} onClose={onClose} />
  );
}

function Shell({ children }: { children: ReactNode }) {
  return (
    <SignedInPage>
      <Card narrow>{children}</Card>
    </SignedInPage>
  );
}

/**
 * The organization's channels and messages (docs/design: Main), loaded when an organization is opened. Behind the
 * messaging feature flag; with it off the organization shows only its name.
 */
function OrganizationHome({
  organization,
  onSwitch,
}: {
  organization: AccountOrganization;
  onSwitch: () => void;
}) {
  const t = useTranslate(messages);
  const messagingEnabled = useMessagingEnabled();
  const [inviting, setInviting] = useState(false);
  const invitationsEnabled = useInvitationsEnabled();
  if (!messagingEnabled) {
    return (
      <Shell>
        <Heading>{organization.name}</Heading>
        <LinkButton onClick={onSwitch}>{t('yourOrganizations')}</LinkButton>
      </Shell>
    );
  }

  return (
    <SignedInPage>
      <Suspense
        fallback={
          <Status>
            <Spinner />
            {t('loading')}
          </Status>
        }
      >
        <Workspace
          organization={{
            id: organization.id,
            name: organization.name,
            cell: { id: organization.cellId, url: organization.cellUrl },
          }}
          onSwitch={onSwitch}
          onInvite={
            invitationsEnabled
              ? () => {
                  setInviting(true);
                }
              : undefined
          }
        />
        {inviting && invitationsEnabled && (
          // Its own boundary: the first load must not replace the workspace (and the focus in it) with a spinner.
          <Suspense fallback={null}>
            <OrganizationInvite
              organization={organization}
              onClose={() => {
                setInviting(false);
              }}
            />
          </Suspense>
        )}
      </Suspense>
    </SignedInPage>
  );
}
