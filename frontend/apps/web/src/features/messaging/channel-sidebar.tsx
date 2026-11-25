import { useId, useState, type MouseEvent } from 'react';
import type { ConnectionStatus } from '@backroom/realtime';
import { Alert, Button, Icon, LinkButton, SoonButton, Spinner, Status, type IconName } from '@backroom/ui';
import { useTranslate } from '@/shared/locale';
import type { Channel } from './api';
import { ChannelMark } from './channel-mark';
import { ConnectionNotice } from './connection-notice';
import { messages } from './messages';

export type ChannelsState = {
  channels: readonly Channel[];
  loading: boolean;
  failed: boolean;
  /** The organization's server cannot be used with this sign-in (its scope is missing). */
  unavailable: boolean;
  /** Desktop: the server waits for a browser confirmation, which the main area offers. */
  signInPending: boolean;
  hasMore: boolean;
  loadingMore: boolean;
  reload: () => void;
  loadMore: () => void;
};

/** The sidebar's shortcuts (docs/design: Main); all of them come in later phases. */
const SHORTCUTS: readonly { key: 'rooms' | 'calendar' | 'threads' | 'saved'; icon: IconName }[] = [
  { key: 'rooms', icon: 'rooms' },
  { key: 'calendar', icon: 'calendar' },
  { key: 'threads', icon: 'threads' },
  { key: 'saved', icon: 'bookmark' },
];

/**
 * docs/design: Main and Sections, the sidebar: shortcuts, the channels section and the direct messages section.
 * Only the channels work in Phase 1; the rest is shown, disabled and marked as coming soon.
 */
export function ChannelSidebar({
  organizationName,
  state,
  selectedId,
  onSelect,
  onAdd,
  onBrowse,
  activity,
  connection,
}: {
  organizationName: string;
  state: ChannelsState;
  selectedId: string | undefined;
  onSelect: (channel: Channel) => void;
  onAdd: () => void;
  onBrowse: () => void;
  /** Channels with new messages since they were last open. */
  activity: ReadonlySet<string>;
  connection: ConnectionStatus;
}) {
  const t = useTranslate(messages);
  const channelsHeading = useId();
  const channelsList = useId();
  const dmsHeading = useId();
  const [collapsed, setCollapsed] = useState(false);
  // A collapsed section still shows the open channel and those with new messages (docs/design: Sections).
  const shown = collapsed
    ? state.channels.filter((channel) => channel.id === selectedId || activity.has(channel.id))
    : state.channels;

  return (
    <nav className="bm-nav" aria-label={organizationName}>
      <ul className="bm-shortcuts">
        {SHORTCUTS.map((shortcut) => (
          <li key={shortcut.key}>
            <SoonItem label={t(shortcut.key)} soon={t('soon')} icon={shortcut.icon} />
          </li>
        ))}
      </ul>
      <div className="bm-scroll">
        <section aria-labelledby={channelsHeading}>
          <div className="bm-section">
            <h2 id={channelsHeading} className="bm-section__title">
              <button
                type="button"
                className="bm-section__toggle"
                aria-expanded={!collapsed}
                aria-controls={channelsList}
                onClick={() => {
                  setCollapsed((value) => !value);
                }}
              >
                <Icon name={collapsed ? 'chevronRight' : 'chevronDown'} size={12} />
                {t('channels')}
              </button>
            </h2>
            <SoonButton
              className="bm-section__action"
              label={t('sectionOptions', { name: t('channels') })}
              soon={t('soon')}
            >
              <Icon name="more" size={14} />
            </SoonButton>
            <button
              type="button"
              className="bm-section__action"
              aria-label={t('sectionAdd', { name: t('channels') })}
              title={t('sectionAdd', { name: t('channels') })}
              onClick={onAdd}
            >
              <Icon name="plus" size={14} />
            </button>
          </div>
          {state.loading && (
            <Status>
              <Spinner />
              {t('loading')}
            </Status>
          )}
          {state.failed && !state.signInPending && (
            <div className="bm-stack">
              <Alert>{t(state.unavailable ? 'serverUnavailable' : 'generic')}</Alert>
              <Button onClick={state.reload}>{t('tryAgain')}</Button>
            </div>
          )}
          {!collapsed && !state.loading && !state.failed && state.channels.length === 0 && (
            <p className="ph-muted bm-section__empty">{t('noChannels')}</p>
          )}
          <ul id={channelsList} className="bm-list">
            {shown.map((channel) => {
              const current = channel.id === selectedId;
              const unread = activity.has(channel.id) && !current;
              return (
                <li key={channel.id}>
                  <button
                    type="button"
                    className={unread ? 'bm-item bm-item--unread' : 'bm-item'}
                    aria-current={current ? 'true' : undefined}
                    onClick={() => {
                      onSelect(channel);
                    }}
                  >
                    <span className="bm-item__icon bm-item__hash">
                      <ChannelMark isPrivate={channel.kind === 'private_channel'} size={15} />
                    </span>
                    <span className="bm-item__text">
                      {channel.kind === 'private_channel' && (
                        <>
                          <span className="bm-sr-only">{t('privateChannel')}</span>{' '}
                        </>
                      )}
                      {channel.name}
                    </span>
                    {unread && (
                      <span className="bm-dot">
                        <span className="bm-sr-only">{t('newActivity')}</span>
                      </span>
                    )}
                  </button>
                </li>
              );
            })}
          </ul>
          {!collapsed && state.hasMore && (
            <LinkButton disabled={state.loadingMore} onClick={state.loadMore}>
              {t('showMoreChannels')}
            </LinkButton>
          )}
          {!collapsed && (
            <button type="button" className="bm-item bm-item--muted" onClick={onBrowse}>
              <span className="bm-item__icon">
                <Icon name="more" size={16} />
              </span>
              <span className="bm-item__text">{t('browseChannels')}</span>
            </button>
          )}
          {!collapsed && (
            <button type="button" className="bm-item bm-item--muted" onClick={onAdd}>
              <span className="bm-item__icon">
                <Icon name="plus" size={16} />
              </span>
              <span className="bm-item__text">{t('addChannel')}</span>
            </button>
          )}
        </section>
        <section aria-labelledby={dmsHeading}>
          <div className="bm-section">
            <h2 id={dmsHeading} className="bm-section__title">
              <span className="bm-section__label">
                <Icon name="chevronDown" size={12} />
                {t('directMessages')}
              </span>
            </h2>
            <SoonButton
              className="bm-section__action"
              label={t('sectionOptions', { name: t('directMessages') })}
              soon={t('soon')}
            >
              <Icon name="more" size={14} />
            </SoonButton>
            <SoonButton
              className="bm-section__action"
              label={t('sectionAdd', { name: t('directMessages') })}
              soon={t('soon')}
            >
              <Icon name="plus" size={14} />
            </SoonButton>
          </div>
          <SoonItem label={t('allDms')} soon={t('soon')} icon="more" muted />
        </section>
      </div>
      <ConnectionNotice status={connection} />
    </nav>
  );
}

/** A sidebar row for a later phase: visible and focusable, disabled, with "coming soon" where a count would be. */
function SoonItem({
  label,
  soon,
  icon,
  muted = false,
}: {
  label: string;
  soon: string;
  icon: IconName;
  muted?: boolean;
}) {
  const soonId = useId();
  const inert = (event: MouseEvent) => {
    event.preventDefault();
  };
  return (
    <button
      type="button"
      className={muted ? 'bm-item bm-item--muted bm-item--soon' : 'bm-item bm-item--soon'}
      aria-label={label}
      aria-disabled="true"
      aria-describedby={soonId}
      onClick={inert}
    >
      <span className="bm-item__icon">
        <Icon name={icon} size={16} />
      </span>
      <span className="bm-item__text">{label}</span>
      <span id={soonId} className="bm-item__meta">
        {soon}
      </span>
    </button>
  );
}
