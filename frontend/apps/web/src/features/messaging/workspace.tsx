import { useCallback, useEffect, useMemo, useState } from 'react';
import { useInfiniteQuery, useQueryClient } from '@tanstack/react-query';
import { Alert, Button, Spinner, Status } from '@backroom/ui';
import { CellSignInPendingError, CellSignInPrompt, CellUnavailableError, useAuth } from '@/features/auth';
import { ApiError } from '@/shared/http';
import { useLocale, useTranslate } from '@/shared/locale';
import { useMessagingApiFactory, type Cell, type Channel, type MessagingApi } from './api';
import { ChannelSidebar, type ChannelsState } from './channel-sidebar';
import { Composer } from './composer';
import { CreateChannelDialog } from './create-channel-dialog';
import { MessageList, type Row } from './message-list';
import { messages } from './messages';
import { NewMessageAnnouncer } from './new-message-announcer';
import { usePendingMessages } from './pending-messages';
import { channelsKey, membersKey, oldestFirst, useChannelMessages } from './queries';
import './messaging.css';

export type WorkspaceOrganization = { id: string; name: string; cell: Cell };

/**
 * The signed-in organization: the channel sidebar and the selected channel's messages (Phase 1, step 7a). Keyed by
 * the organization, so nothing of one (chosen channel, unsent messages, running requests) carries over to another.
 */
export function Workspace(props: { organization: WorkspaceOrganization; onSwitch: () => void }) {
  return <OrganizationWorkspace key={props.organization.id} {...props} />;
}

function OrganizationWorkspace({
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
        {channels.signInPending ? (
          <CellSignInPrompt onDismiss={channels.reload} />
        ) : selected ? (
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
    unavailable: query.error instanceof CellUnavailableError,
    signInPending: query.error instanceof CellSignInPendingError,
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

/** Names of the organization's members, loaded page by page only while an author on screen is unknown. */
const MAX_MEMBER_PAGES = 5;

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
  const members = useInfiniteQuery({
    queryKey: membersKey(orgId),
    queryFn: ({ pageParam, signal }) => api.listMembers(orgId, pageParam, signal),
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (last) => last.next ?? undefined,
    staleTime: 5 * 60_000,
    maxPages: MAX_MEMBER_PAGES,
  });

  const names = useMemo(
    () =>
      new Map(
        members.data?.pages.flatMap((page) =>
          page.members.map((member) => [member.accountId, member.displayName]),
        ),
      ),
    [members.data],
  );
  // From the sign-in's claims (the token's `sub`); the members' own entry (the only one with an email) is the
  // fallback for a sign-in client that does not know it.
  const ownAccountId = useMemo(
    () =>
      user.accountId ??
      members.data?.pages
        .flatMap((page) => page.members)
        .find((member) => member.email !== null && member.email.toLowerCase() === user.email.toLowerCase())
        ?.accountId,
    [members.data, user.accountId, user.email],
  );
  const authorName = useCallback((accountId: string) => names.get(accountId) ?? t('member'), [names, t]);

  const pendingHere = pending.forChannel(channel.id);
  const confirmed = useMemo(() => oldestFirst(query.data), [query.data]);
  const rows = useMemo<Row[]>(() => {
    const known = new Set(confirmed.map((message) => message.id));
    const knownClient = new Set(confirmed.map((message) => message.clientMessageId));
    // Confirmed messages not in a fetched page yet are shown from the pending list (only next to the newest page).
    const overlay = query.hasPreviousPage
      ? []
      : pendingHere.flatMap((item) =>
          item.message && !known.has(item.message.id)
            ? [{ key: item.message.id, message: item.message }]
            : [],
        );
    const unsent = pendingHere
      .filter((item) => item.status !== 'confirmed' && !knownClient.has(item.clientMessageId))
      .map((item) => ({ key: `pending:${item.clientMessageId}`, pending: item }));
    return [...confirmed.map((message) => ({ key: message.id, message })), ...overlay, ...unsent];
  }, [confirmed, pendingHere, query.hasPreviousPage]);

  // Confirmed messages leave the pending list once a fetched page has them.
  const { prune } = pending;
  useEffect(() => {
    prune(channel.id, new Set(confirmed.map((message) => message.id)));
  }, [confirmed, channel.id, prune]);

  // Names of authors not known yet: the next page of members, a few pages at most.
  const {
    hasNextPage: moreMembers,
    isFetchingNextPage: loadingMembers,
    fetchNextPage: fetchMembers,
  } = members;
  const unknownAuthor = confirmed.some((message) => !names.has(message.authorAccountId));
  useEffect(() => {
    if (
      unknownAuthor &&
      moreMembers &&
      !loadingMembers &&
      members.data &&
      members.data.pages.length < MAX_MEMBER_PAGES
    ) {
      void fetchMembers();
    }
  }, [unknownAuthor, moreMembers, loadingMembers, members.data, fetchMembers]);

  const { fetchNextPage, fetchNewer } = query;
  const loadEarlier = useCallback(() => {
    void fetchNextPage();
  }, [fetchNextPage]);
  const loadNewer = useCallback(() => {
    void fetchNewer();
  }, [fetchNewer]);
  const { retry: retryMessage, discard: discardMessage } = pending;
  const retry = useCallback(
    (clientMessageId: string) => {
      retryMessage(channel.id, clientMessageId);
    },
    [retryMessage, channel.id],
  );
  const discard = useCallback(
    (clientMessageId: string) => {
      discardMessage(channel.id, clientMessageId);
    },
    [discardMessage, channel.id],
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
            hasNewer={query.hasPreviousPage}
            loadingNewer={query.isFetchingPreviousPage}
            onLoadNewer={loadNewer}
            authorName={authorName}
            ownAccountId={ownAccountId}
            locale={locale}
            onRetry={retry}
            onDiscard={discard}
          />
          <NewMessageAnnouncer
            rows={rows}
            ownAccountId={ownAccountId}
            authorName={authorName}
            active={!query.hasPreviousPage}
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
