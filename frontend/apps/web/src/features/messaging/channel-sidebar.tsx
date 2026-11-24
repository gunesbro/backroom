import type { ConnectionStatus } from '@backroom/realtime';
import { Alert, Button, LinkButton, Spinner, Status } from '@backroom/ui';
import { useTranslate } from '@/shared/locale';
import type { Channel } from './api';
import { ConnectionNotice } from './connection-notice';
import { messages } from './messages';

export type ChannelsState = {
  channels: readonly Channel[];
  loading: boolean;
  failed: boolean;
  hasMore: boolean;
  loadingMore: boolean;
  reload: () => void;
  loadMore: () => void;
};

/** docs/design: Main, the "Kanallar" section of the sidebar. */
export function ChannelSidebar({
  organizationName,
  state,
  selectedId,
  onSelect,
  onAdd,
  onSwitch,
  activity,
  connection,
}: {
  organizationName: string;
  state: ChannelsState;
  selectedId: string | undefined;
  onSelect: (channel: Channel) => void;
  onAdd: () => void;
  onSwitch: () => void;
  /** Channels with new messages since they were last open. */
  activity: ReadonlySet<string>;
  connection: ConnectionStatus;
}) {
  const t = useTranslate(messages);
  return (
    <aside className="bm-sidebar" aria-label={organizationName}>
      <h1 className="bm-org">{organizationName}</h1>
      <nav aria-labelledby="bm-channels-heading">
        <div className="bm-section">
          <h2 id="bm-channels-heading" className="bm-section__title">
            {t('channels')}
          </h2>
          <button type="button" className="bm-add" onClick={onAdd}>
            <span aria-hidden="true">+</span> {t('addChannel')}
          </button>
        </div>
        {state.loading && (
          <Status>
            <Spinner />
            {t('loading')}
          </Status>
        )}
        {state.failed && (
          <div className="bm-stack">
            <Alert>{t('generic')}</Alert>
            <Button onClick={state.reload}>{t('tryAgain')}</Button>
          </div>
        )}
        {!state.loading && !state.failed && state.channels.length === 0 && (
          <p className="ph-muted">{t('noChannels')}</p>
        )}
        <ul className="bm-channels">
          {state.channels.map((channel) => (
            <li key={channel.id}>
              <button
                type="button"
                className="bm-channel"
                aria-current={channel.id === selectedId ? 'true' : undefined}
                onClick={() => {
                  onSelect(channel);
                }}
              >
                <span aria-hidden="true">#</span>
                <span className="bm-channel__name">{channel.name}</span>
                {activity.has(channel.id) && channel.id !== selectedId && (
                  <span className="bm-dot">
                    <span className="bm-sr-only">{t('newActivity')}</span>
                  </span>
                )}
              </button>
            </li>
          ))}
        </ul>
        {state.hasMore && (
          <LinkButton disabled={state.loadingMore} onClick={state.loadMore}>
            {t('showMoreChannels')}
          </LinkButton>
        )}
      </nav>
      <ConnectionNotice status={connection} />
      <LinkButton onClick={onSwitch}>{t('yourOrganizations')}</LinkButton>
    </aside>
  );
}
