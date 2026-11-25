import { useEffect, useId, useMemo, useRef, useState, type ReactNode, type SubmitEvent } from 'react';
import { useInfiniteQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useVirtualizer } from '@tanstack/react-virtual';
import { Alert, Avatar, Button, Dialog, LinkButton, Note, Spinner, Status } from '@backroom/ui';
import { ApiError } from '@/shared/http';
import { useLocale, useTranslate } from '@/shared/locale';
import type { Channel, ConversationDetail, ConversationMember, MessagingApi } from './api';
import {
  descriptionProblem,
  MAX_DESCRIPTION_UNITS,
  MAX_NAME_UNITS,
  nameProblem,
  normalizeText,
  sameNameExists,
} from './channel-fields';
import { ChannelMark } from './channel-mark';
import { conversationErrorText, isConversationGone } from './conversation-errors';
import { MemberPicker, type PickedMember } from './member-picker';
import { messages } from './messages';
import {
  conversationKey,
  conversationMembersKey,
  conversationsKey,
  archivedConversationsKey,
  useConversation,
  useMyPermissions,
} from './queries';

/** Every member row is this tall, so the list can be virtualized without measuring rows. */
const MEMBER_ROW_HEIGHT = 40;
const MANAGE_PUBLIC = 'channels.manage-public';

type Mode =
  | { kind: 'view' }
  | { kind: 'edit' }
  | { kind: 'add' }
  | { kind: 'leave' }
  | { kind: 'archive' }
  | { kind: 'transfer'; member: PickedMember };

type Action =
  | { do: 'update'; change: { name?: string; description?: string } }
  | { do: 'add'; accountIds: readonly string[] }
  | { do: 'leave' }
  | { do: 'archive' }
  | { do: 'unarchive' }
  | { do: 'transfer'; accountId: string };

const VIEW: Mode = { kind: 'view' };

/**
 * "Kanal bilgisi" (docs/design: Main, the button in the channel's header): what the channel is, who is in it, and
 * what the person may do with it. Everyone in it may leave; members of a private channel add people; its owner, or
 * for a public channel whoever holds `channels.manage-public`, renames, describes, archives and unarchives it; the
 * owner of a private channel hands it over. Removing other people is not offered: the server keeps it switched off.
 */
export function ChannelInfoDialog({
  api,
  orgId,
  channel,
  ownAccountId,
  memberName,
  existingNames,
  onClose,
  onLeaving,
  onLeft,
  onGone,
}: {
  api: MessagingApi;
  orgId: string;
  channel: Channel;
  ownAccountId: string | undefined;
  /** The display name of an account, or a general word for one not known. */
  memberName: (accountId: string) => string;
  /** Names of the other channels the person can see, to warn about a private channel with the same name. */
  existingNames: readonly string[];
  onClose: () => void;
  /** The person is about to leave the channel: the live event may arrive before the answer. */
  onLeaving: () => void;
  /** The person left the channel. */
  onLeft: () => void;
  /** The person may not read the channel any more. */
  onGone: () => void;
}) {
  const t = useTranslate(messages);
  const locale = useLocale();
  const queryClient = useQueryClient();
  const [mode, setMode] = useState<Mode>(VIEW);
  const detailQuery = useConversation(api, orgId, channel.id);
  const detail = detailQuery.data;
  const isPrivate = (detail?.kind ?? channel.kind) === 'private_channel';

  const membersQuery = useInfiniteQuery({
    queryKey: conversationMembersKey(orgId, channel.id),
    queryFn: ({ pageParam, signal }) => api.listConversationMembers(orgId, channel.id, pageParam, signal),
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (last) => last.next ?? undefined,
  });
  const members = useMemo(
    () => membersQuery.data?.pages.flatMap((page) => page.members) ?? [],
    [membersQuery.data],
  );
  const memberIds = useMemo(() => new Set(members.map((member) => member.accountId)), [members]);

  // Asked only for a public channel: a private one is managed by its owner, whatever their role.
  const permissions = useMyPermissions(api, orgId, detail !== undefined && !isPrivate);

  const gone = isConversationGone(detailQuery.error) || isConversationGone(membersQuery.error);
  useEffect(() => {
    if (gone) onGone();
  }, [gone, onGone]);

  const mutation = useMutation({
    mutationFn: async (action: Action) => {
      if (action.do === 'update') {
        const updated = await api.updateConversation(orgId, channel.id, action.change);
        queryClient.setQueryData(conversationKey(orgId, channel.id), updated);
      } else if (action.do === 'add') {
        await api.addConversationMembers(orgId, channel.id, action.accountIds);
      } else if (action.do === 'leave') {
        onLeaving();
        await api.leaveConversation(orgId, channel.id);
      } else if (action.do === 'archive') {
        await api.archiveConversation(orgId, channel.id);
      } else if (action.do === 'unarchive') {
        await api.unarchiveConversation(orgId, channel.id);
      } else {
        await api.transferOwnership(orgId, channel.id, action.accountId);
      }

      return action;
    },
    onSuccess: (action) => {
      if (action.do === 'leave') {
        onLeft();
        return;
      }

      // The live event says the same, but it is a hint: what is shown here is read again.
      void queryClient.invalidateQueries({ queryKey: conversationKey(orgId, channel.id) });
      void queryClient.invalidateQueries({ queryKey: conversationMembersKey(orgId, channel.id) });
      // The sidebar shows the name, and leaves archived channels out.
      if (action.do !== 'add' && action.do !== 'transfer') {
        void queryClient.invalidateQueries({ queryKey: conversationsKey(orgId) });
        void queryClient.invalidateQueries({ queryKey: archivedConversationsKey(orgId) });
      }

      setMode(VIEW);
    },
    onError: (error) => {
      if (isConversationGone(error)) onGone();
    },
  });

  // Each view replaces the one before it, and the button that opened it with it: focus moves to the new view so the
  // keyboard stays inside the dialog and a screen reader reads its heading.
  const sectionRef = useRef<HTMLDivElement>(null);
  const firstView = useRef(true);
  useEffect(() => {
    if (firstView.current) {
      firstView.current = false;
      return;
    }

    sectionRef.current?.focus();
  }, [mode]);

  const show = (next: Mode) => {
    mutation.reset();
    setMode(next);
  };
  const back = () => {
    show(VIEW);
  };

  const name = detail?.name ?? channel.name;
  const title = (
    <>
      <ChannelMark isPrivate={isPrivate} label={t('privateChannel')} /> {name}
    </>
  );
  const alert = mutation.error ? <Alert>{conversationErrorText(mutation.error, t, locale)}</Alert> : null;

  let content: ReactNode;
  if (!detail) {
    content = (
      <>
        <div className="ph-dialog__body">
          {detailQuery.isPending && (
            <Status>
              <Spinner />
              {t('loading')}
            </Status>
          )}
          {detailQuery.isError && !gone && (
            <div className="bm-stack">
              <Alert>{t('generic')}</Alert>
              <Button
                onClick={() => {
                  void detailQuery.refetch();
                }}
              >
                {t('tryAgain')}
              </Button>
            </div>
          )}
        </div>
        <div className="ph-dialog__foot">
          <Button onClick={onClose}>{t('close')}</Button>
        </div>
      </>
    );
  } else if (mode.kind === 'edit') {
    content = (
      <EditForm
        key="edit"
        detail={detail}
        isPrivate={isPrivate}
        existingNames={existingNames}
        sectionRef={sectionRef}
        busy={mutation.isPending}
        error={mutation.error}
        onChange={() => {
          mutation.reset();
        }}
        onCancel={back}
        onSave={(change) => {
          if (Object.keys(change).length === 0) back();
          else mutation.mutate({ do: 'update', change });
        }}
      />
    );
  } else if (mode.kind === 'add') {
    content = (
      <AddForm
        key="add"
        api={api}
        orgId={orgId}
        memberIds={memberIds}
        sectionRef={sectionRef}
        busy={mutation.isPending}
        alert={alert}
        onChange={() => {
          mutation.reset();
        }}
        onCancel={back}
        onAdd={(accountIds) => {
          mutation.mutate({ do: 'add', accountIds });
        }}
      />
    );
  } else if (mode.kind !== 'view') {
    const isOwner = detail.role === 'owner';
    const confirm =
      mode.kind === 'leave'
        ? {
            question: t('leaveConfirm', { name }),
            notes: [
              ...(isPrivate && isOwner && detail.memberCount > 1 ? [t('leaveOwnerNote')] : []),
              ...(isPrivate && detail.memberCount <= 1 ? [t('leaveLastNote')] : []),
              t(isPrivate ? 'leavePrivateNote' : 'leavePublicNote'),
            ],
            label: t('leaveChannel'),
            action: { do: 'leave' } as const,
          }
        : mode.kind === 'archive'
          ? {
              question: t('archiveConfirm', { name }),
              notes: [t('archiveNote')],
              label: t('archive'),
              action: { do: 'archive' } as const,
            }
          : {
              question: t('transferConfirm', { name: mode.member.displayName }),
              notes: [t('transferNote')],
              label: t('transferOwnership'),
              action: { do: 'transfer', accountId: mode.member.accountId } as const,
            };
    content = (
      <Confirm
        key={mode.kind}
        sectionRef={sectionRef}
        question={confirm.question}
        notes={confirm.notes}
        label={confirm.label}
        cancelLabel={t('cancel')}
        busy={mutation.isPending}
        alert={alert}
        onCancel={back}
        onConfirm={() => {
          mutation.mutate(confirm.action);
        }}
      />
    );
  } else {
    const isOwner = isPrivate && detail.role === 'owner';
    const isMember = detail.role !== null;
    const archived = detail.archivedAt !== null;
    const canManage = isPrivate ? isOwner : permissions.data?.includes(MANAGE_PUBLIC) === true;
    content = (
      <>
        <div className="ph-dialog__body">
          <div ref={sectionRef} className="bm-info" tabIndex={-1} role="group" aria-label={t('channelInfo')}>
            <div className="bm-info__about">
              {archived && <span className="bm-badge">{t('archived')}</span>}
              <p className={detail.description ? 'bm-info__description' : 'ph-muted'}>
                {detail.description ?? t('noDescription')}
              </p>
              {canManage && !archived && (
                <LinkButton
                  onClick={() => {
                    show({ kind: 'edit' });
                  }}
                >
                  {t('edit')}
                </LinkButton>
              )}
            </div>
            <MemberList
              count={detail.memberCount}
              loading={membersQuery.isPending}
              failed={membersQuery.isError && !membersQuery.data && !gone}
              onRetry={() => {
                void membersQuery.refetch();
              }}
              hasMore={membersQuery.hasNextPage}
              loadingMore={membersQuery.isFetchingNextPage}
              onMore={() => {
                void membersQuery.fetchNextPage();
              }}
              members={members}
              renderMember={(member) => {
                const own = member.accountId === ownAccountId;
                const displayName = memberName(member.accountId);
                return (
                  <>
                    <Avatar name={displayName} toneKey={member.accountId} size="sm" />
                    <span className="bm-member__name">
                      {displayName} {own && <span className="ph-muted">{t('you')}</span>}
                    </span>
                    {isPrivate && member.role === 'owner' && (
                      <span className="bm-badge">{t('channelOwner')}</span>
                    )}
                    {isOwner && !own && member.role !== 'owner' && (
                      <Button
                        className="bm-member__action"
                        aria-label={t('transferTo', { name: displayName })}
                        onClick={() => {
                          show({ kind: 'transfer', member: { accountId: member.accountId, displayName } });
                        }}
                      >
                        {t('transferOwnership')}
                      </Button>
                    )}
                  </>
                );
              }}
            />
            {isPrivate && isMember && !archived && (
              <div>
                <Button
                  onClick={() => {
                    show({ kind: 'add' });
                  }}
                >
                  {t('addPeople')}
                </Button>
              </div>
            )}
            {alert}
          </div>
        </div>
        <div className="ph-dialog__foot">
          {isMember && (
            <Button
              className="bm-danger"
              onClick={() => {
                show({ kind: 'leave' });
              }}
            >
              {t('leaveChannel')}
            </Button>
          )}
          {canManage &&
            (archived ? (
              <Button
                disabled={mutation.isPending}
                onClick={() => {
                  mutation.mutate({ do: 'unarchive' });
                }}
              >
                {t('unarchive')}
              </Button>
            ) : (
              <Button
                onClick={() => {
                  show({ kind: 'archive' });
                }}
              >
                {t('archive')}
              </Button>
            ))}
          <Button onClick={onClose}>{t('close')}</Button>
        </div>
      </>
    );
  }

  return (
    <Dialog title={title} closeLabel={t('close')} onClose={onClose} wide>
      {content}
    </Dialog>
  );
}

/**
 * The members, virtualized: only the rows in view are drawn, however many pages are loaded. Rows have one height;
 * each says its place in the whole list, as only some of them are in the document.
 */
function MemberList({
  count,
  members,
  renderMember,
  loading,
  failed,
  onRetry,
  hasMore,
  loadingMore,
  onMore,
}: {
  /** Members of the channel, loaded or not. */
  count: number;
  members: readonly ConversationMember[];
  renderMember: (member: ConversationMember) => ReactNode;
  loading: boolean;
  failed: boolean;
  onRetry: () => void;
  hasMore: boolean;
  loadingMore: boolean;
  onMore: () => void;
}) {
  const t = useTranslate(messages);
  const headingId = useId();
  const scrollRef = useRef<HTMLDivElement>(null);
  // The virtualizer is used only in this component and its functions are not passed to memoized children.
  // eslint-disable-next-line react-hooks/incompatible-library
  const virtualizer = useVirtualizer({
    count: members.length,
    getScrollElement: () => scrollRef.current,
    estimateSize: () => MEMBER_ROW_HEIGHT,
    getItemKey: (index) => members[index]?.accountId ?? index,
    overscan: 6,
  });
  return (
    <section className="bm-info__members" aria-labelledby={headingId}>
      <h3 id={headingId} className="bm-info__heading">
        {t('members')}{' '}
        <span className="bm-info__count">
          {count === 1 ? t('memberCountOne') : t('memberCount', { count })}
        </span>
      </h3>
      {loading && (
        <Status>
          <Spinner />
          {t('loading')}
        </Status>
      )}
      {failed && (
        <div className="bm-stack">
          <Alert>{t('generic')}</Alert>
          <Button onClick={onRetry}>{t('tryAgain')}</Button>
        </div>
      )}
      {/* Focusable, so the list can be scrolled with the keyboard when no row has a button. */}
      <div ref={scrollRef} className="bm-members" tabIndex={0} role="group" aria-labelledby={headingId}>
        <ul
          className="bm-members__rows"
          aria-labelledby={headingId}
          style={{ height: `${String(virtualizer.getTotalSize())}px` }}
        >
          {virtualizer.getVirtualItems().map((item) => {
            const member = members[item.index];
            return (
              member && (
                <li
                  key={item.key}
                  className="bm-member"
                  aria-setsize={Math.max(count, members.length)}
                  aria-posinset={item.index + 1}
                  style={{ transform: `translateY(${String(item.start)}px)` }}
                >
                  {renderMember(member)}
                </li>
              )
            );
          })}
        </ul>
      </div>
      {hasMore && (
        <LinkButton disabled={loadingMore} onClick={onMore}>
          {t('showMoreMembers')}
        </LinkButton>
      )}
    </section>
  );
}

function EditForm({
  detail,
  isPrivate,
  existingNames,
  sectionRef,
  busy,
  error,
  onChange,
  onCancel,
  onSave,
}: {
  detail: ConversationDetail;
  isPrivate: boolean;
  existingNames: readonly string[];
  sectionRef: { current: HTMLDivElement | null };
  busy: boolean;
  error: unknown;
  onChange: () => void;
  onCancel: () => void;
  /** Only what changed; empty when nothing did. */
  onSave: (change: { name?: string; description?: string }) => void;
}) {
  const t = useTranslate(messages);
  const locale = useLocale();
  const headingId = useId();
  const nameId = useId();
  const nameErrorId = useId();
  const warningId = useId();
  const descriptionId = useId();
  const descriptionErrorId = useId();
  const [name, setName] = useState(detail.name ?? '');
  const [description, setDescription] = useState(detail.description ?? '');
  const [invalid, setInvalid] = useState({ name: false, description: false });

  const submit = (event: SubmitEvent<HTMLFormElement>) => {
    event.preventDefault();
    if (busy) return;
    const problems = { name: nameProblem(name), description: descriptionProblem(description) };
    setInvalid(problems);
    if (problems.name || problems.description) return;
    const nextName = normalizeText(name);
    const nextDescription = normalizeText(description);
    onSave({
      ...(nextName === detail.name ? {} : { name: nextName }),
      ...(nextDescription === (detail.description ?? '') ? {} : { description: nextDescription }),
    });
  };

  const code = error instanceof ApiError ? error.code : undefined;
  const nameError =
    invalid.name || code === 'messaging.channel_name_invalid'
      ? t('nameInvalid')
      : code === 'messaging.channel_name_taken'
        ? t('nameTaken')
        : undefined;
  const descriptionError =
    invalid.description || code === 'messaging.channel_description_invalid'
      ? t('descriptionInvalid')
      : undefined;
  const sameName = isPrivate && normalizeText(name) !== detail.name && sameNameExists(name, existingNames);
  const nameDescribedBy = [nameError && nameErrorId, sameName && warningId].filter(Boolean).join(' ');

  return (
    <form className="ph-dialog__form" onSubmit={submit} noValidate aria-busy={busy}>
      <div className="ph-dialog__body">
        <div ref={sectionRef} className="bm-info" tabIndex={-1} role="group" aria-labelledby={headingId}>
          <h3 id={headingId} className="bm-info__heading">
            {t('editChannel')}
          </h3>
          <div className="ph-field">
            <label className="ph-field__label" htmlFor={nameId}>
              {t('channelName')}
            </label>
            <div className="ph-prefixed">
              <span className="ph-prefixed__prefix" aria-hidden="true">
                #
              </span>
              <input
                id={nameId}
                value={name}
                maxLength={MAX_NAME_UNITS}
                autoComplete="off"
                required
                aria-invalid={nameError ? true : undefined}
                aria-describedby={nameDescribedBy === '' ? undefined : nameDescribedBy}
                onChange={(event) => {
                  setName(event.target.value);
                  setInvalid((current) => ({ ...current, name: false }));
                  onChange();
                }}
              />
            </div>
            {nameError && (
              <span id={nameErrorId} className="ph-field__error" role="alert">
                {nameError}
              </span>
            )}
            {sameName && (
              <span id={warningId} className="ph-field__hint" role="status">
                {t('sameNameWarning')}
              </span>
            )}
          </div>
          <div className="ph-field">
            <label className="ph-field__label" htmlFor={descriptionId}>
              {t('description')} <span className="bm-optional">{t('optional')}</span>
            </label>
            <input
              id={descriptionId}
              className="ph-input"
              value={description}
              maxLength={MAX_DESCRIPTION_UNITS}
              autoComplete="off"
              aria-invalid={descriptionError ? true : undefined}
              aria-describedby={descriptionError ? descriptionErrorId : undefined}
              onChange={(event) => {
                setDescription(event.target.value);
                setInvalid((current) => ({ ...current, description: false }));
                onChange();
              }}
            />
            {descriptionError && (
              <span id={descriptionErrorId} className="ph-field__error" role="alert">
                {descriptionError}
              </span>
            )}
          </div>
          {error !== null && error !== undefined && !nameError && !descriptionError && (
            <Alert>{conversationErrorText(error, t, locale)}</Alert>
          )}
        </div>
      </div>
      <div className="ph-dialog__foot">
        <Button onClick={onCancel}>{t('cancel')}</Button>
        <Button type="submit" variant="primary" disabled={busy}>
          {t('save')}
        </Button>
      </div>
    </form>
  );
}

function AddForm({
  api,
  orgId,
  memberIds,
  sectionRef,
  busy,
  alert,
  onChange,
  onCancel,
  onAdd,
}: {
  api: MessagingApi;
  orgId: string;
  /** The members loaded so far: they are left out of the choices; the server skips the rest. */
  memberIds: ReadonlySet<string>;
  sectionRef: { current: HTMLDivElement | null };
  busy: boolean;
  alert: ReactNode;
  onChange: () => void;
  onCancel: () => void;
  onAdd: (accountIds: readonly string[]) => void;
}) {
  const t = useTranslate(messages);
  const [people, setPeople] = useState<readonly PickedMember[]>([]);
  const submit = (event: SubmitEvent<HTMLFormElement>) => {
    event.preventDefault();
    if (!busy && people.length > 0) onAdd(people.map((person) => person.accountId));
  };

  return (
    <form className="ph-dialog__form" onSubmit={submit} noValidate aria-busy={busy}>
      <div className="ph-dialog__body">
        <div ref={sectionRef} className="bm-info" tabIndex={-1} role="group" aria-label={t('addPeople')}>
          <MemberPicker
            api={api}
            orgId={orgId}
            label={t('addPeople')}
            selected={people}
            onChange={(next) => {
              setPeople(next);
              onChange();
            }}
            excluded={memberIds}
          />
          {alert}
        </div>
      </div>
      <div className="ph-dialog__foot">
        <Button onClick={onCancel}>{t('cancel')}</Button>
        <Button type="submit" variant="primary" disabled={busy || people.length === 0}>
          {t('add')}
        </Button>
      </div>
    </form>
  );
}

function Confirm({
  sectionRef,
  question,
  notes,
  label,
  cancelLabel,
  busy,
  alert,
  onCancel,
  onConfirm,
}: {
  sectionRef: { current: HTMLDivElement | null };
  question: string;
  notes: readonly string[];
  label: string;
  cancelLabel: string;
  busy: boolean;
  alert: ReactNode;
  onCancel: () => void;
  onConfirm: () => void;
}) {
  const questionId = useId();
  return (
    <>
      <div className="ph-dialog__body">
        <div ref={sectionRef} className="bm-info" tabIndex={-1} role="group" aria-labelledby={questionId}>
          <h3 id={questionId} className="bm-info__heading">
            {question}
          </h3>
          {notes.map((note) => (
            <Note key={note}>{note}</Note>
          ))}
          {alert}
        </div>
      </div>
      <div className="ph-dialog__foot">
        <Button onClick={onCancel}>{cancelLabel}</Button>
        <Button variant="primary" disabled={busy} onClick={onConfirm}>
          {label}
        </Button>
      </div>
    </>
  );
}
