import { Avatar, Icon, MenuButton, OrgMark, SoonButton, type MenuEntry } from '@backroom/ui';
import { useAuth } from '@/features/auth';
import { EmojiText } from '@/features/emoji';
import { PresenceDot, useStatusMenu } from '@/features/presence';
import { useTranslate } from '@/shared/locale';
import type { AccountOrganization } from './api';
import { messages } from './messages';
import { useSignOut } from './sign-out';

/**
 * docs/design: OrgNotif, the organization menu at the start of the top bar: the person's organizations (the
 * current one checked), inviting, settings (later phase), and creating or joining one.
 */
export function OrganizationSwitcher({
  current,
  organizations,
  onChoose,
  onInvite,
  onCreate,
}: {
  current: AccountOrganization;
  organizations: readonly AccountOrganization[];
  onChoose: (id: string) => void;
  /** Undefined hides the entry (the invitation feature flag is off). */
  onInvite?: (() => void) | undefined;
  /** Opens the screen to set up an organization or join one with an invitation link. */
  onCreate: () => void;
}) {
  const t = useTranslate(messages);
  const entries: MenuEntry[] = [
    {
      kind: 'group',
      id: 'organizations',
      label: t('myOrganizations'),
      entries: organizations.map((organization) => ({
        kind: 'item' as const,
        id: organization.id,
        label: organization.name,
        icon: <OrgMark name={organization.name} />,
        checked: organization.id === current.id,
        onSelect: () => {
          if (organization.id !== current.id) onChoose(organization.id);
        },
      })),
    },
    { kind: 'separator', id: 'sep-1' },
    ...(onInvite
      ? [
          {
            kind: 'item' as const,
            id: 'invite',
            label: t('invitePeopleTo', { organization: current.name }),
            icon: <Icon name="userPlus" />,
            onSelect: onInvite,
          },
        ]
      : []),
    {
      kind: 'item',
      id: 'settings',
      label: t('organizationSettings'),
      icon: <Icon name="settings" />,
      soon: t('soon'),
    },
    { kind: 'separator', id: 'sep-2' },
    {
      kind: 'item',
      id: 'create',
      label: t('createOrganization'),
      icon: <Icon name="plus" />,
      onSelect: onCreate,
    },
    {
      kind: 'item',
      id: 'join',
      label: t('joinWithLink'),
      icon: <Icon name="link" />,
      onSelect: onCreate,
    },
  ];

  return (
    <MenuButton
      label={t('switchOrganization')}
      description={current.name}
      buttonClassName="bo-switcher"
      menuClassName="bo-switcher__menu"
      entries={entries}
      button={
        <>
          <OrgMark name={current.name} />
          <span className="bo-switcher__name">{current.name}</span>
          <Icon name="chevronUpDown" />
        </>
      }
    />
  );
}

/**
 * docs/design: Main and Thread, the account at the foot of the sidebar: the status and profile menu (status with the
 * presence feature flag, sign-out; settings come later) and the microphone, audio and settings buttons of later phases.
 */
export function AccountBar() {
  const t = useTranslate(messages);
  const { user } = useAuth();
  const signOut = useSignOut();
  const soon = t('soon');
  const toneKey = user.accountId ?? user.email;
  const status = useStatusMenu();
  const statusEntries: MenuEntry[] = status?.entries ?? [
    { kind: 'item', id: 'online', label: t('online'), soon },
    { kind: 'item', id: 'away', label: t('away'), soon },
    { kind: 'item', id: 'dnd', label: t('doNotDisturb'), soon },
    { kind: 'separator', id: 'sep-1' },
    { kind: 'item', id: 'custom', label: t('customStatus'), soon },
  ];
  const entries: MenuEntry[] = [
    ...statusEntries,
    { kind: 'item', id: 'room', label: t('backToRoom'), soon },
    { kind: 'separator', id: 'sep-2' },
    { kind: 'item', id: 'profile', label: t('profileSettings'), soon },
    { kind: 'item', id: 'sign-out', label: t('signOut'), onSelect: signOut },
  ];

  return (
    <section className="bo-account" aria-label={t('account')}>
      <MenuButton
        label={t('accountMenu')}
        description={
          status ? [user.name, status.presenceLabel, status.line].filter(Boolean).join(', ') : user.name
        }
        buttonClassName="bo-account__button"
        menuClassName="bo-account__menu"
        placement="above"
        entries={entries}
        header={
          <div className="bo-account__header">
            <Avatar name={user.name} toneKey={toneKey} size="lg" />
            <span className="bo-account__who">
              <b>{user.name}</b>
              <small>{user.email}</small>
            </span>
          </div>
        }
        button={
          <>
            {status ? (
              <span className="bp-avatar">
                <Avatar name={user.name} toneKey={toneKey} />
                {/* Named in the button's description: content inside a labelled button is not read. */}
                <PresenceDot presence={status.presence} />
              </span>
            ) : (
              <Avatar name={user.name} toneKey={toneKey} />
            )}
            <span className="bo-account__who">
              <b>{user.name}</b>
              <small>{status?.line ? <EmojiText text={status.line} /> : user.email}</small>
            </span>
          </>
        }
      />
      {status?.overlay}
      <SoonButton className="bo-account__tool" label={t('microphone')} soon={soon}>
        <Icon name="mic" />
      </SoonButton>
      <SoonButton className="bo-account__tool" label={t('audioOutput')} soon={soon}>
        <Icon name="headphones" />
      </SoonButton>
      <SoonButton className="bo-account__tool" label={t('settings')} soon={soon} placement="top-start">
        <Icon name="settings" />
      </SoonButton>
    </section>
  );
}
