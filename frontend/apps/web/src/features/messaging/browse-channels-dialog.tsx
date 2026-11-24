import { useMemo } from 'react';
import { useInfiniteQuery, useMutation } from '@tanstack/react-query';
import { Alert, Button, Dialog, LinkButton, Spinner, Status } from '@backroom/ui';
import { useTranslate } from '@/shared/locale';
import type { Channel, MessagingApi } from './api';
import { browseChannelsKey } from './queries';
import { messages } from './messages';

/**
 * The public channels of the organization (docs/contracts/phase-2.md, "HTTP APIs": <code>GET /channels</code>), with a
 * button to join those the person is not in. Joining adds the channel to the sidebar and opens it.
 */
export function BrowseChannelsDialog({
  api,
  orgId,
  joinedIds,
  onClose,
  onJoined,
}: {
  api: MessagingApi;
  orgId: string;
  /** Channels the person is already in. */
  joinedIds: readonly string[];
  onClose: () => void;
  onJoined: (channel: Channel) => void;
}) {
  const t = useTranslate(messages);
  const query = useInfiniteQuery({
    queryKey: browseChannelsKey(orgId),
    queryFn: ({ pageParam, signal }) => api.browseChannels(orgId, pageParam, signal),
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

  return (
    <Dialog title={t('browseChannels')} closeLabel={t('close')} onClose={onClose}>
      <div className="ph-dialog__body">
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
        {query.data && channels.length === 0 && <p className="ph-muted bm-empty">{t('noChannels')}</p>}
        <ul className="bm-list">
          {channels.map((channel) => (
            <li key={channel.id} className="bm-browse__row">
              <span className="bm-item__text">
                <span aria-hidden="true">#</span> {channel.name}
              </span>
              {joined.has(channel.id) ? (
                <span className="ph-muted">{t('joined')}</span>
              ) : (
                <Button
                  disabled={join.isPending}
                  aria-label={t('joinChannel', { name: channel.name })}
                  onClick={() => {
                    join.mutate(channel);
                  }}
                >
                  {t('join')}
                </Button>
              )}
            </li>
          ))}
        </ul>
        {query.hasNextPage && (
          <LinkButton disabled={query.isFetchingNextPage} onClick={() => void query.fetchNextPage()}>
            {t('showMoreChannels')}
          </LinkButton>
        )}
        {join.isError && <Alert>{t('generic')}</Alert>}
      </div>
      <div className="ph-dialog__foot">
        <Button onClick={onClose}>{t('close')}</Button>
      </div>
    </Dialog>
  );
}
