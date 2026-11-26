import { useEffect, useMemo, useRef, useState } from 'react';
import { useInfiniteQuery, useMutation } from '@tanstack/react-query';
import { Alert, Button, Dialog, LinkButton, Spinner, Status } from '@backroom/ui';
import { useTranslate } from '@/shared/locale';
import type { Channel, MessagingApi } from './api';
import { ChannelMark } from './channel-mark';
import { archivedConversationsKey, browseChannelsKey } from './queries';
import { messages } from './messages';

/**
 * The public channels of the organization (docs/contracts/phase-2.md, "HTTP APIs": <code>GET /channels</code>), with a
 * button to open those the person is not in (read without joining) and one to join them. Joining adds the channel to
 * the sidebar and opens it. A second view lists the person's archived channels
 * (<code>GET /conversations?archived=true</code>), which the sidebar leaves out: opening one shows it read-only, and
 * those allowed can unarchive it from its info.
 */
export function BrowseChannelsDialog({
  api,
  orgId,
  joinedIds,
  onClose,
  onJoined,
  onOpen,
}: {
  api: MessagingApi;
  orgId: string;
  /** Channels the person is already in. */
  joinedIds: readonly string[];
  onClose: () => void;
  onJoined: (channel: Channel) => void;
  onOpen: (channel: Channel) => void;
}) {
  const t = useTranslate(messages);
  const [archived, setArchived] = useState(false);
  // The link that switches the view is replaced by the other one: focus moves to the new view's list.
  const viewRef = useRef<HTMLDivElement>(null);
  const firstView = useRef(true);
  useEffect(() => {
    if (firstView.current) {
      firstView.current = false;
      return;
    }

    viewRef.current?.focus();
  }, [archived]);

  const query = useInfiniteQuery({
    queryKey: archived ? archivedConversationsKey(orgId) : browseChannelsKey(orgId),
    queryFn: async ({ pageParam, signal }) => {
      if (!archived) return api.browseChannels(orgId, pageParam, signal);
      const page = await api.listConversations(orgId, pageParam, signal, { archived: true });
      return { channels: page.conversations, next: page.next };
    },
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (last) => last.next ?? undefined,
  });
  const channels = useMemo(() => query.data?.pages.flatMap((page) => page.channels) ?? [], [query.data]);
  const joined = useMemo(() => new Set(joinedIds), [joinedIds]);
  const join = useMutation({
    mutationFn: async (channel: Channel) => {
      await api.joinConversation(orgId, channel.id);
      return channel;
    },
    onSuccess: onJoined,
  });
  const title = t(archived ? 'archivedChannels' : 'browseChannels');

  return (
    <Dialog title={title} closeLabel={t('close')} onClose={onClose}>
      <div className="ph-dialog__body">
        <div ref={viewRef} className="bm-info" tabIndex={-1} role="group" aria-label={title}>
          {query.isPending && (
            <Status>
              <Spinner />
              {t('loading')}
            </Status>
          )}
          {query.isError && !query.data && (
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
          {query.data && channels.length === 0 && (
            <p className="ph-muted bm-empty">{t(archived ? 'noArchivedChannels' : 'noChannels')}</p>
          )}
          <ul className="bm-list">
            {channels.map((channel) => (
              <li key={channel.id} className="bm-browse__row">
                <span className="bm-item__text">
                  <ChannelMark isPrivate={channel.kind === 'private_channel'} label={t('privateChannel')} />{' '}
                  {channel.name}
                </span>
                {archived ? (
                  <Button
                    aria-label={t('openChannel', { name: channel.name })}
                    onClick={() => {
                      onOpen(channel);
                    }}
                  >
                    {t('open')}
                  </Button>
                ) : joined.has(channel.id) ? (
                  <span className="ph-muted">{t('joined')}</span>
                ) : (
                  <span className="bm-browse__actions">
                    <Button
                      aria-label={t('openChannel', { name: channel.name })}
                      onClick={() => {
                        onOpen(channel);
                      }}
                    >
                      {t('open')}
                    </Button>
                    <Button
                      disabled={join.isPending}
                      aria-label={t('joinChannel', { name: channel.name })}
                      onClick={() => {
                        join.mutate(channel);
                      }}
                    >
                      {t('join')}
                    </Button>
                  </span>
                )}
              </li>
            ))}
          </ul>
          {query.hasNextPage && (
            <LinkButton disabled={query.isFetchingNextPage} onClick={() => void query.fetchNextPage()}>
              {t('showMoreChannels')}
            </LinkButton>
          )}
          {join.isError && !archived && <Alert>{t('generic')}</Alert>}
        </div>
      </div>
      <div className="ph-dialog__foot">
        <span className="bm-foot-start">
          <LinkButton
            onClick={() => {
              setArchived((current) => !current);
            }}
          >
            {t(archived ? 'browseChannels' : 'archivedChannels')}
          </LinkButton>
        </span>
        <Button onClick={onClose}>{t('close')}</Button>
      </div>
    </Dialog>
  );
}
