import {
  lazy,
  Suspense,
  useCallback,
  useEffect,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
  type ReactNode,
} from 'react';
import { useInfiniteQuery, useQueryClient } from '@tanstack/react-query';
import { Alert, Button, Icon, SoonButton, Spinner, Status } from '@backroom/ui';
import { CellSignInPendingError, CellSignInPrompt, CellUnavailableError, useAuth } from '@/features/auth';
import { config } from '@/config';
import { useLocale, useTranslate } from '@/shared/locale';
import { useMessagingApiFactory, type Cell, type Channel, type MessagingApi } from './api';
import { ChannelMark } from './channel-mark';
import { ChannelSidebar, type ChannelsState } from './channel-sidebar';
import { Composer } from './composer';
import { isConversationGone } from './conversation-errors';
import { CreateChannelDialog } from './create-channel-dialog';
import { DeleteMessageDialog } from './delete-message-dialog';
import {
  applyMessageChange,
  deleteMessageOptimistically,
  forgetConversation,
  restoreMessage,
} from './live-cache';
import { isMessageDeleted, messageChangeErrorText } from './message-errors';
import { MessageList, type MessageEditing, type Row } from './message-list';
import { editableBlocks, type Block } from './rich-text/blocks';
import { rowKey } from './row-key';
import { messages } from './messages';
import { PreviewBar } from './preview-bar';
import { NewMessageAnnouncer } from './new-message-announcer';
import { usePendingMessages } from './pending-messages';
import { BrowseChannelsDialog } from './browse-channels-dialog';
import {
  conversationKey,
  conversationsKey,
  MAX_MEMBER_PAGES,
  messagesKey,
  oldestFirst,
  useChannelMessages,
  useConversation,
  useOrganizationMembers,
} from './queries';
import { useLiveUpdates } from './use-live-updates';
import './messaging.css';
import './channels.css';

/** The channel's details and what can be done with it: loaded when it is first opened. */
const ChannelInfoDialog = lazy(() =>
  import('./channel-info-dialog').then(({ ChannelInfoDialog: dialog }) => ({ default: dialog })),
);

export type WorkspaceOrganization = { id: string; name: string; cell: Cell };

type WorkspaceProps = {
  organization: WorkspaceOrganization;
  /** The organization switcher at the start of the top bar; the organization's name when omitted. */
  switcher?: ReactNode;
  /** The signed-in account at the foot of the sidebar (its menu signs out). */
  account?: ReactNode;
};

/**
 * The signed-in organization (docs/design: Main): the top bar, the sidebar with the channels and the account, and
 * the selected channel's messages. Keyed by the organization, so nothing of one (chosen channel, unsent messages,
 * running requests) carries over to another.
 */
export function Workspace(props: WorkspaceProps) {
  return <OrganizationWorkspace key={props.organization.id} {...props} />;
}

function OrganizationWorkspace({ organization, switcher, account }: WorkspaceProps) {
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
  const [browsing, setBrowsing] = useState(false);
  /** The channel whose info is open. */
  const [infoFor, setInfoFor] = useState<string | undefined>();
  /** Said once, politely, when the open channel was taken away. */
  const [notice, setNotice] = useState('');
  /** The conversation the person last lost: not opened by default while the list still has it. */
  const [dropped, setDropped] = useState<string | undefined>();
  const first = channels.channels.find((channel) => channel.id !== dropped);
  // The open channel is chosen once, not derived from the list: it stays open when it leaves the list (an archived
  // channel does), so its banner and its info can still be reached.
  if (!chosen && first) setChosen(first);
  const selected = chosen ?? first;
  const selectedId = selected?.id;
  const pending = usePendingMessages(api, orgId);
  const { user } = useAuth();
  const selectedRef = useRef(selectedId);
  useLayoutEffect(() => {
    selectedRef.current = selectedId;
  });
  /** Conversations the person is leaving themself: losing them needs no notice. */
  const leaving = useRef(new Set<string>());
  const lostAccess = t('lostAccess');
  const drop = useCallback(
    (conversationId: string) => {
      const own = leaving.current.delete(conversationId);
      if (!own && selectedRef.current === conversationId) setNotice(lostAccess);
      setDropped(conversationId);
      setChosen((current) => (current?.id === conversationId ? undefined : current));
      setInfoFor((current) => (current === conversationId ? undefined : current));
    },
    [lostAccess],
  );
  /** The server says the conversation cannot be read (any more): forget it and leave it. */
  const gone = useCallback(
    (conversationId: string) => {
      drop(conversationId);
      forgetConversation(queryClient, orgId, conversationId);
    },
    [drop, queryClient, orgId],
  );
  const noteLeaving = useCallback((conversationId: string, started: boolean) => {
    if (started) leaving.current.add(conversationId);
    else leaving.current.delete(conversationId);
  }, []);
  const channelIds = useMemo(() => channels.channels.map((channel) => channel.id), [channels.channels]);
  const live = useLiveUpdates({
    cell: organization.cell,
    api,
    orgId,
    channelIds,
    selectedId,
    ownAccountId: user.accountId,
    detailedId: infoFor,
    onRemoved: drop,
  });
  const { clearActivity } = live;
  const select = useCallback(
    (channel: Channel) => {
      clearActivity(channel.id);
      setChosen(channel);
      setNotice('');
    },
    [clearActivity],
  );
  const channelNames = useMemo(() => channels.channels.map((channel) => channel.name), [channels.channels]);
  const otherNames = useMemo(
    () => channels.channels.filter((channel) => channel.id !== selectedId).map((channel) => channel.name),
    [channels.channels, selectedId],
  );

  return (
    <div className="bm-app">
      <header className="bm-top">
        {/* The page's title for screen readers; the switcher shows the name. */}
        <h1 className="bm-sr-only">{organization.name}</h1>
        <div className="bm-top__org">
          {switcher ?? (
            <span className="bm-top__name" aria-hidden="true">
              {organization.name}
            </span>
          )}
        </div>
        <SoonButton className="bm-search" label={t('search')} soon={t('soon')} placement="bottom" showLabel>
          <span className="bm-search__text">{t('search')}</span>
        </SoonButton>
        <SoonButton className="bm-bell" label={t('notifications')} soon={t('soon')} placement="bottom-end">
          <Icon name="bell" />
        </SoonButton>
      </header>
      <div className="bm-body">
        <div className="bm-side">
          <ChannelSidebar
            organizationName={organization.name}
            state={channels}
            selectedId={selectedId}
            onSelect={select}
            onAdd={() => {
              setCreating(true);
            }}
            onBrowse={() => {
              setBrowsing(true);
            }}
            activity={live.activity}
            connection={live.status}
          />
          {account}
        </div>
        <main className="bm-main">
          <div className="bm-notice" role="status">
            {notice}
          </div>
          {channels.signInPending ? (
            <CellSignInPrompt onDismiss={channels.reload} />
          ) : selected ? (
            <ChannelView
              key={selected.id}
              api={api}
              orgId={orgId}
              channel={selected}
              joined={channelIds.includes(selected.id)}
              pending={pending}
              otherNames={otherNames}
              infoOpen={infoFor === selected.id}
              onInfoOpenChange={(open) => {
                setInfoFor(open ? selected.id : undefined);
              }}
              onGone={gone}
              onLeaving={noteLeaving}
            />
          ) : (
            !channels.loading && !channels.failed && <p className="ph-muted bm-empty">{t('selectChannel')}</p>
          )}
        </main>
      </div>
      {browsing && (
        <BrowseChannelsDialog
          api={api}
          orgId={orgId}
          joinedIds={channelIds}
          onClose={() => {
            setBrowsing(false);
          }}
          onJoined={(channel) => {
            void queryClient.invalidateQueries({ queryKey: conversationsKey(orgId) });
            void queryClient.invalidateQueries({ queryKey: conversationKey(orgId, channel.id) });
            clearActivity(channel.id);
            setChosen(channel);
            setBrowsing(false);
          }}
          onOpen={(channel) => {
            select(channel);
            setBrowsing(false);
          }}
        />
      )}
      {creating && (
        <CreateChannelDialog
          api={api}
          orgId={orgId}
          existingNames={channelNames}
          onClose={() => {
            setCreating(false);
          }}
          onCreated={(channel) => {
            void queryClient.invalidateQueries({ queryKey: conversationsKey(orgId) });
            setChosen(channel);
            setNotice('');
            setCreating(false);
          }}
        />
      )}
    </div>
  );
}

/** The person's own conversations, oldest first, paged by the server's `next`. */
function useChannels(api: MessagingApi, orgId: string): ChannelsState {
  const query = useInfiniteQuery({
    queryKey: conversationsKey(orgId),
    queryFn: ({ pageParam, signal }) => api.listConversations(orgId, pageParam, signal),
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (last) => last.next ?? undefined,
  });
  const channels = useMemo(() => query.data?.pages.flatMap((page) => page.conversations) ?? [], [query.data]);
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

function ChannelView({
  api,
  orgId,
  channel,
  joined,
  pending,
  otherNames,
  infoOpen,
  onInfoOpenChange,
  onGone,
  onLeaving,
}: {
  api: MessagingApi;
  orgId: string;
  channel: Channel;
  /** The channel is in the person's own list (the sidebar). */
  joined: boolean;
  pending: ReturnType<typeof usePendingMessages>;
  /** Names of the other channels the person can see. */
  otherNames: readonly string[];
  infoOpen: boolean;
  onInfoOpenChange: (open: boolean) => void;
  /** The channel cannot be read any more, or the person left it. */
  onGone: (conversationId: string) => void;
  /** The person is about to leave the channel themself (`started`), or that leave failed. */
  onLeaving: (conversationId: string, started: boolean) => void;
}) {
  const t = useTranslate(messages);
  const { user } = useAuth();
  const query = useChannelMessages(api, orgId, channel.id);
  // Names of the organization's members, loaded page by page only while an author on screen is unknown.
  const members = useOrganizationMembers(api, orgId);
  // The channel's kind, archive state and member count; the messages do not wait for it.
  const detail = useConversation(api, orgId, channel.id);
  const info = detail.data;
  const name = info?.name ?? channel.name;
  const isPrivate = (info?.kind ?? channel.kind) === 'private_channel';
  const archived = info ? info.archivedAt !== null : false;
  // A public channel read without having joined it: no composer, a bar to join instead.
  const previewing = !joined && info !== undefined && info.role === null && info.kind === 'public_channel';
  // Until the detail says whether the person has joined, a channel outside the list shows neither composer nor bar.
  const stateKnown = joined || info !== undefined;
  // Set when the person joins from the bar: the composer that replaces it takes the keyboard focus.
  const [joinedFromBar, setJoinedFromBar] = useState(false);

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
  const mentionName = useCallback((accountId: string) => names.get(accountId), [names]);
  const authorName = useCallback((accountId: string) => names.get(accountId) ?? t('member'), [names, t]);

  const pendingHere = pending.forChannel(channel.id);

  // A conversation the person lost access to answers 404 everywhere: leave it instead of showing an error.
  const lost = isConversationGone(query.error) || isConversationGone(detail.error);
  useEffect(() => {
    if (lost) onGone(channel.id);
  }, [lost, onGone, channel.id]);
  // A send refused because the channel was archived or taken away meanwhile: read its state again, which shows the
  // archive banner or leaves the channel.
  const refused = pendingHere.some(
    (item) =>
      item.status === 'failed' &&
      (item.code === 'messaging.conversation_archived' || item.code === 'messaging.conversation_not_found'),
  );
  const { refetch: refetchDetail } = detail;
  useEffect(() => {
    if (refused) void refetchDetail();
  }, [refused, refetchDetail]);
  const confirmed = useMemo(() => oldestFirst(query.data), [query.data]);
  const rows = useMemo<Row[]>(() => {
    const known = new Set(confirmed.map((message) => message.id));
    const knownClient = new Set(confirmed.map((message) => message.clientMessageId));
    // Confirmed messages not in a fetched page yet are shown from the pending list (only next to the newest page).
    const overlay = query.hasPreviousPage
      ? []
      : pendingHere.flatMap((item) =>
          item.message && !known.has(item.message.id)
            ? [{ key: rowKey(item.message), message: item.message }]
            : [],
        );
    const unsent = pendingHere
      .filter((item) => item.status !== 'confirmed' && !knownClient.has(item.clientMessageId))
      .map((item) => ({
        key: rowKey({ authorAccountId: ownAccountId ?? '', clientMessageId: item.clientMessageId, id: '' }),
        pending: item,
      }));
    return [...confirmed.map((message) => ({ key: rowKey(message), message })), ...overlay, ...unsent];
  }, [confirmed, pendingHere, query.hasPreviousPage, ownAccountId]);

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
  const unknownAuthor = confirmed.some(
    (message) => !message.deletedAt && !names.has(message.authorAccountId),
  );
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
  // A send refused because the person has not joined the channel: join it, then send the same message again. A failed
  // join leaves the message as it is, so the action can be tried again.
  const queryClient = useQueryClient();
  const join = useCallback(
    (clientMessageId: string) => {
      api
        .joinConversation(orgId, channel.id)
        .then(() => {
          void queryClient.invalidateQueries({ queryKey: conversationsKey(orgId) });
          retryMessage(channel.id, clientMessageId);
        })
        .catch(() => undefined);
    },
    [api, orgId, channel.id, queryClient, retryMessage],
  );

  // Edit and delete one's own messages (Phase 2 step 9): off without its flag, and the editor is the rich text one.
  const editEnabled = config.messageEditEnabled && config.richTextEnabled;
  const [editingId, setEditingId] = useState<string | undefined>();
  /** The message the person asked to delete, until they answer the question. */
  const [deleting, setDeleting] = useState<string | undefined>();
  /** What went wrong with the last delete (an edit shows its error in its own editor). */
  const [changeError, setChangeError] = useState('');
  const locale = useLocale();
  const cancelEdit = useCallback(() => {
    setEditingId(undefined);
  }, []);
  const saveEdit = useCallback(
    async (messageId: string, blocks: Block[]): Promise<string | undefined> => {
      try {
        const edited = await api.editMessage(orgId, messageId, blocks);
        // A fetch that started before the answer could return the old content and overwrite it.
        await queryClient.cancelQueries({ queryKey: messagesKey(orgId, channel.id) });
        applyMessageChange(queryClient, orgId, channel.id, edited);
        setEditingId(undefined);
        return undefined;
      } catch (error) {
        if (isMessageDeleted(error)) {
          // Deleted meanwhile (a live event may not have come yet): there is nothing left to edit.
          deleteMessageOptimistically(queryClient, orgId, channel.id, messageId);
          setEditingId(undefined);
          return undefined;
        }

        return messageChangeErrorText(error, t, locale);
      }
    },
    [api, orgId, channel.id, queryClient, t, locale],
  );
  const confirmDelete = useCallback(
    async (messageId: string) => {
      setDeleting(undefined);
      setChangeError('');
      await queryClient.cancelQueries({ queryKey: messagesKey(orgId, channel.id) });
      // Shown as deleted at once; put back if the server refuses.
      const original = deleteMessageOptimistically(queryClient, orgId, channel.id, messageId);
      try {
        await api.deleteMessage(orgId, messageId);
      } catch (error) {
        if (isMessageDeleted(error)) return;
        if (original) restoreMessage(queryClient, orgId, channel.id, original);
        setChangeError(messageChangeErrorText(error, t, locale));
      }
    },
    [api, orgId, channel.id, queryClient, t, locale],
  );
  const editing = useMemo<MessageEditing | undefined>(
    () =>
      editEnabled
        ? {
            editingId,
            start: setEditingId,
            cancel: cancelEdit,
            save: saveEdit,
            remove: setDeleting,
          }
        : undefined,
    [editEnabled, editingId, cancelEdit, saveEdit],
  );
  /** ArrowUp in the empty composer: the person's newest own message in the loaded list, if it can be edited. */
  const editNewest = useCallback(() => {
    if (query.hasPreviousPage || ownAccountId === undefined) return;
    const newest = confirmed.findLast(
      (message) => !message.deletedAt && message.authorAccountId === ownAccountId,
    );
    if (newest && editableBlocks(newest.blocks, newest.text) !== null) setEditingId(newest.id);
  }, [query.hasPreviousPage, ownAccountId, confirmed]);

  return (
    <>
      <header className="bm-header">
        <h2 className="bm-header__title">
          <ChannelMark isPrivate={isPrivate} label={t('privateChannel')} /> {name}
        </h2>
        {info && (
          <span className="ph-muted bm-header__count">
            {info.memberCount === 1 ? t('memberCountOne') : t('memberCount', { count: info.memberCount })}
          </span>
        )}
        <button
          type="button"
          className="bm-header__button"
          onClick={() => {
            onInfoOpenChange(true);
          }}
        >
          {t('channelInfo')}
        </button>
      </header>
      {query.isPending && (
        <Status>
          <Spinner />
          {t('loading')}
        </Status>
      )}
      {query.isError && !query.data && !lost && (
        <div className="bm-stack">
          <Alert>{t('generic')}</Alert>
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
            channelName={name}
            rows={rows}
            hasEarlier={query.hasNextPage}
            loadingEarlier={query.isFetchingNextPage}
            onLoadEarlier={loadEarlier}
            hasNewer={query.hasPreviousPage}
            loadingNewer={query.isFetchingPreviousPage}
            onLoadNewer={loadNewer}
            authorName={authorName}
            mentionName={mentionName}
            ownAccountId={ownAccountId}
            ownName={user.name}
            locale={locale}
            onRetry={retry}
            onDiscard={discard}
            onJoin={join}
            editing={editing}
          />
          <NewMessageAnnouncer
            rows={rows}
            ownAccountId={ownAccountId}
            authorName={authorName}
            active={!query.hasPreviousPage}
          />
        </>
      )}
      {changeError && (
        <div className="bm-stack">
          <Alert>{changeError}</Alert>
        </div>
      )}
      {deleting !== undefined && (
        <DeleteMessageDialog
          onConfirm={() => {
            void confirmDelete(deleting);
          }}
          onCancel={() => {
            setDeleting(undefined);
          }}
        />
      )}
      {archived ? (
        <p className="bm-archived">{t('archivedBanner')}</p>
      ) : previewing ? (
        <PreviewBar
          join={() => api.joinConversation(orgId, channel.id)}
          onJoined={() => {
            setJoinedFromBar(true);
            void queryClient.invalidateQueries({ queryKey: conversationsKey(orgId) });
            void queryClient.invalidateQueries({ queryKey: conversationKey(orgId, channel.id) });
          }}
        />
      ) : stateKnown ? (
        <Composer
          focusOnMount={joinedFromBar}
          channelName={name}
          {...(editing ? { onEditLast: editNewest } : {})}
          onSend={(text, blocks) => {
            pending.send(channel.id, text, blocks);
          }}
        />
      ) : null}
      {infoOpen && (
        <Suspense fallback={null}>
          <ChannelInfoDialog
            api={api}
            orgId={orgId}
            channel={channel}
            ownAccountId={ownAccountId}
            memberName={authorName}
            existingNames={otherNames}
            onClose={() => {
              onInfoOpenChange(false);
            }}
            onLeaving={() => {
              onLeaving(channel.id, true);
            }}
            onLeaveFailed={() => {
              onLeaving(channel.id, false);
            }}
            onLeft={() => {
              onGone(channel.id);
            }}
            onGone={() => {
              onGone(channel.id);
            }}
          />
        </Suspense>
      )}
    </>
  );
}
