// The page of the browser smoke test (apps/web/e2e): the real message list with 200 fake messages and no server. A
// React Profiler keeps the commit count and the summed render time on `window.__listProfile` for the test and for
// load-tests/tools/message-list-profile.mjs. Nothing imports this file, so no build contains it.
import {
  Profiler,
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
  type ProfilerOnRenderCallback,
} from 'react';
import { createRoot } from 'react-dom/client';
import '@fontsource/ibm-plex-sans/400.css';
import '@backroom/ui';
import { LocaleProvider } from '@/shared/locale-provider';
import type { Message } from '../api';
import { DeleteMessageDialog } from '../delete-message-dialog';
import { MessageList, type MessageEditing, type MessageListHandle, type Row } from '../message-list';
import '../messaging.css';

type ListProfile = { commits: number; duration: number; mountDuration: number; reset: () => void };
declare global {
  interface Window {
    __listProfile?: ListProfile;
  }
}

const profile: ListProfile = {
  commits: 0,
  duration: 0,
  mountDuration: 0,
  reset: () => {
    profile.commits = 0;
    profile.duration = 0;
  },
};
window.__listProfile = profile;

const onRender: ProfilerOnRenderCallback = (_id, phase, actualDuration) => {
  if (phase === 'mount') profile.mountDuration = actualDuration;
  profile.commits += 1;
  profile.duration += actualDuration;
};

const AUTHORS = ['00000000-0000-4000-8000-000000000002', '00000000-0000-4000-8000-000000000003'];
const NAMES: Record<string, string> = { [AUTHORS[0] ?? '']: 'Veli', [AUTHORS[1] ?? '']: 'Ayse' };
const COUNT = Number(new URLSearchParams(location.search).get('count') ?? 200);
const OWN = AUTHORS[new URLSearchParams(location.search).has('own') ? 1 : 0];
const START = Date.parse('2026-11-01T09:00:00Z');
const initialRows: Row[] = Array.from({ length: COUNT }, (_, index): Row => {
  const message: Message = {
    id: `m-${String(index)}`,
    seq: index + 1,
    clientMessageId: `c-${String(index)}`,
    authorAccountId: AUTHORS[index % 2] ?? '',
    text: `message ${String(index + 1)}${index % 3 === 0 ? ' with a longer text that wraps over more than one line when the window is narrow enough to need it' : ''}`,
    createdAt: new Date(START + index * 37 * 60_000).toISOString(),
  };
  return { key: message.clientMessageId, message };
});
const noop = () => undefined;

/**
 * The list with edit and delete working in memory, driven as the workspace drives them: the editor just started takes
 * the focus once, delete asks in the real dialog, and the row takes the focus back when the dialog closes.
 */
// eslint-disable-next-line react-refresh/only-export-components -- a test page: the whole page reloads on a change
function Page() {
  const [rows, setRows] = useState(initialRows);
  const [editingId, setEditingId] = useState<string>();
  const [deleting, setDeleting] = useState<string>();
  const focusEditorFor = useRef<string>(undefined);
  const refocusId = useRef<string>(undefined);
  const listRef = useRef<MessageListHandle>(null);
  const change = useCallback((id: string, update: (message: Message) => Message) => {
    setRows((current) =>
      current.map((row) => (row.message?.id === id ? { key: row.key, message: update(row.message) } : row)),
    );
  }, []);
  useEffect(() => {
    if (deleting !== undefined) return;
    const id = refocusId.current;
    refocusId.current = undefined;
    const key = initialRows.find((row) => row.message?.id === id)?.key;
    if (key !== undefined) listRef.current?.focusRow(key);
  }, [deleting]);
  const editing = useMemo<MessageEditing>(
    () => ({
      start: (id) => {
        focusEditorFor.current = id;
        setEditingId(id);
      },
      cancel: () => {
        setEditingId(undefined);
      },
      save: (id, blocks) => {
        change(id, (message) => ({ ...message, blocks, editedAt: new Date().toISOString() }));
        setEditingId(undefined);
        return Promise.resolve(undefined);
      },
      remove: (id) => {
        refocusId.current = id;
        setDeleting(id);
      },
      takeFocus: (id) => {
        const mine = focusEditorFor.current === id;
        if (mine) focusEditorFor.current = undefined;
        return mine;
      },
    }),
    [change],
  );

  return (
    <>
      <MessageList
        ref={listRef}
        channelName="general"
        rows={rows}
        hasEarlier={false}
        loadingEarlier={false}
        onLoadEarlier={noop}
        hasNewer={false}
        loadingNewer={false}
        onLoadNewer={noop}
        authorName={(id) => NAMES[id] ?? 'Someone'}
        ownAccountId={OWN}
        editing={editing}
        editingId={editingId}
        ownName="Alice"
        locale="en"
        onRetry={noop}
        onDiscard={noop}
        onJoin={noop}
      />
      {deleting !== undefined && (
        <DeleteMessageDialog
          onConfirm={() => {
            setDeleting(undefined);
            change(deleting, (message) => ({ ...message, deletedAt: new Date().toISOString() }));
          }}
          onCancel={() => {
            setDeleting(undefined);
          }}
        />
      )}
    </>
  );
}

const root = document.getElementById('root');
if (!root) throw new Error('Root element #root is missing.');
createRoot(root).render(
  <LocaleProvider locale="en">
    <div
      style={{
        display: 'flex',
        flexDirection: 'column',
        height: 600,
        maxWidth: 720,
        margin: '0 auto',
        background: 'var(--ph-panel)',
      }}
    >
      <Profiler id="list" onRender={onRender}>
        <Page />
      </Profiler>
    </div>
  </LocaleProvider>,
);
