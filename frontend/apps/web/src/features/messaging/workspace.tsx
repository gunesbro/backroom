import { useCallback, useMemo, useState } from 'react';
import { useInfiniteQuery, useQuery, useQueryClient } from '@tanstack/react-query';
import { Alert, Button, Spinner, Status } from '@backroom/ui';
import { useAuth } from '@/features/auth';
import { ApiError } from '@/shared/http';
import { useLocale, useTranslate } from '@/shared/locale';
import { useMessagingApiFactory, type Cell, type Channel, type MessagingApi } from './api';
import { ChannelSidebar, type ChannelsState } from './channel-sidebar';
import { Composer } from './composer';
import { CreateChannelDialog } from './create-channel-dialog';
import { MessageList, type Row } from './message-list';
import { messages } from './messages';
import { usePendingMessages } from './pending-messages';
import { channelsKey, membersKey, oldestFirst, useChannelMessages } from './queries';
import './messaging.css';

export type WorkspaceOrganization = { id: string; name: string; cell: Cell };

/** The signed-in organization: the channel sidebar and the selected channel's messages (Phase 1, step 7a). */
export function Workspace({
  organization,
  onSwitch,
}: {
  organization: WorkspaceOrganization;
  onSwitch: () => void;
}) {
  const t = useTranslate(messages);
  const factory = useMessagingApiFactory();
  const api = useMemo(
    () => factory(organization.cell),
    // The cell's id and address identify it; the object itself may be new on every render.
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [factory, organization.cell.id, organization.cell.url],
  );
  const queryClient = useQueryClient();
  const orgId = organization.id;
  const channels = useChannels(api, orgId);
  const [chosen, setChosen] = useState<Channel | undefined>();
  const [creating, setCreating] = useState(false);
  const selected = chosen ?? channels.channels[0];
  const pending = usePendingMessages(api, orgId);

  return (
    <div className="bm-workspace">
      <ChannelSidebar
        organizationName={organization.name}
        state={channels}
        selectedId={selected?.id}
        onSelect={setChosen}
        onAdd={() => {
          setCreating(true);
        }}
        onSwitch={onSwitch}
      />
      <section className="bm-main">
        {selected ? (
          <ChannelView key={selected.id} api={api} orgId={orgId} channel={selected} pending={pending} />
        ) : (
          !channels.loading && !channels.failed && <p className="ph-muted bm-empty">{t('selectChannel')}</p>
        )}
      </section>
      {creating && (
        <CreateChannelDialog
          api={api}
          orgId={orgId}
          onClose={() => {
            setCreating(false);
          }}
          onCreated={(channel) => {
            void queryClient.invalidateQueries({ queryKey: channelsKey(orgId) });
            setChosen(channel);
            setCreating(false);
          }}
        />
      )}
    </div>
  );
}

/** The organization's channels, oldest first, paged by the server's `next`. */
function useChannels(api: MessagingApi, orgId: string): ChannelsState {
  const query = useInfiniteQuery({
    queryKey: channelsKey(orgId),
    queryFn: ({ pageParam, signal }) => api.listChannels(orgId, pageParam, signal),
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (last) => last.next ?? undefined,
  });
  const channels = useMemo(() => query.data?.pages.flatMap((page) => page.channels) ?? [], [query.data]);
  const { refetch, fetchNextPage } = query;
  return {
    channels,
    loading: query.isPending,
    failed: query.isError && !query.data,
    hasMore: query.hasNextPage,
    loadingMore: query.isFetchingNextPage,
    reload: useCallback(() => {
      void refetch();
    }, [refetch]),
    loadMore: useCallback(() => {
      void fetchNextPage();
    }, [fetchNextPage]),
  };
}

function ChannelView({
  api,
  orgId,
  channel,
  pending,
}: {
  api: MessagingApi;
  orgId: string;
  channel: Channel;
  pending: ReturnType<typeof usePendingMessages>;
}) {
  const t = useTranslate(messages);
  const locale = useLocale();
  const { user } = useAuth();
  const query = useChannelMessages(api, orgId, channel.id);
  const members = useQuery({
    queryKey: membersKey(orgId),
    queryFn: ({ signal }) => api.listMembers(orgId, signal),
    staleTime: 5 * 60_000,
  });

  const names = useMemo(
    () => new Map(members.data?.members.map((member) => [member.accountId, member.displayName])),
    [members.data],
  );
  // Only the caller's own entry carries an email, which tells which account is theirs.
  const ownAccountId = useMemo(
    () =>
      members.data?.members.find(
        (member) => member.email !== null && member.email.toLowerCase() === user.email.toLowerCase(),
      )?.accountId,
    [members.data, user.email],
  );
  const authorName = useCallback((accountId: string) => names.get(accountId) ?? t('member'), [names, t]);

  const pendingHere = pending.forChannel(channel.id);
  const rows = useMemo<Row[]>(() => {
    const confirmed = oldestFirst(query.data);
    const known = new Set(confirmed.map((message) => message.clientMessageId));
    return [
      ...confirmed.map((message) => ({ key: message.id, message })),
      // A message that came back through a refetch before its send finished is shown once.
      ...pendingHere
        .filter((item) => !known.has(item.clientMessageId))
        .map((item) => ({ key: `pending:${item.clientMessageId}`, pending: item })),
    ];
  }, [query.data, pendingHere]);

  const { fetchNextPage } = query;
  const loadEarlier = useCallback(() => {
    void fetchNextPage();
  }, [fetchNextPage]);
  const retry = useCallback(
    (clientMessageId: string) => {
      pending.retry(channel.id, clientMessageId);
    },
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [pending.retry, channel.id],
  );
  const discard = useCallback(
    (clientMessageId: string) => {
      pending.discard(channel.id, clientMessageId);
    },
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [pending.discard, channel.id],
  );

  return (
    <>
      <header className="bm-header">
        <h2 className="bm-header__title">
          <span aria-hidden="true">#</span> {channel.name}
        </h2>
      </header>
      {query.isPending && (
        <Status>
          <Spinner />
          {t('loading')}
        </Status>
      )}
      {query.isError && !query.data && (
        <div className="bm-stack">
          <Alert>
            {query.error instanceof ApiError && query.error.code === 'messaging.channel_not_found'
              ? t('channelGone')
              : t('generic')}
          </Alert>
          <Button
            onClick={() => {
              void query.refetch();
            }}
          >
            {t('tryAgain')}
          </Button>
        </div>
      )}
      {query.data && (
        <>
          {rows.length === 0 && <p className="ph-muted bm-empty">{t('emptyChannel')}</p>}
          <MessageList
            channelName={channel.name}
            rows={rows}
            hasEarlier={query.hasNextPage}
            loadingEarlier={query.isFetchingNextPage}
            onLoadEarlier={loadEarlier}
            authorName={authorName}
            ownAccountId={ownAccountId}
            locale={locale}
            onRetry={retry}
            onDiscard={discard}
          />
        </>
      )}
      <Composer
        channelName={channel.name}
        onSend={(text) => {
          pending.send(channel.id, text);
        }}
      />
    </>
  );
}
