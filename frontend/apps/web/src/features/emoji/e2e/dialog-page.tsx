// The page of the dialog browser smoke test (apps/web/e2e): the shared modal dialog with the emoji picker inside it,
// with no server. Nothing imports this file, so no build contains it.
import { StrictMode, useRef, useState } from 'react';
import { createRoot } from 'react-dom/client';
import '@fontsource/ibm-plex-sans/400.css';
import { Dialog } from '@backroom/ui';
import { LocaleProvider } from '@/shared/locale-provider';
import { EmojiPicker } from '../emoji-picker';

// eslint-disable-next-line react-refresh/only-export-components -- a test page: the whole page reloads on a change
function Page() {
  const [open, setOpen] = useState(false);
  const [closed, setClosed] = useState(0);
  const nameRef = useRef<HTMLInputElement>(null);
  return (
    <LocaleProvider locale="en">
      <main style={{ maxWidth: 640, margin: '0 auto' }}>
        <h1>Dialog smoke test</h1>
        <button
          type="button"
          onClick={() => {
            setOpen(true);
          }}
        >
          Open dialog
        </button>
        <button type="button">Behind the dialog</button>
        <output aria-label="Times closed">{closed}</output>
        {open && (
          <Dialog
            title="Smoke dialog"
            closeLabel="Close"
            initialFocus={nameRef}
            onClose={() => {
              setOpen(false);
              setClosed((count) => count + 1);
            }}
          >
            <div className="ph-dialog__body">
              <label>
                Name <input ref={nameRef} />
              </label>
              <EmojiPicker autoFocus={false} onPick={() => undefined} />
            </div>
          </Dialog>
        )}
      </main>
    </LocaleProvider>
  );
}

const root = document.getElementById('root');
if (!root) throw new Error('Root element #root is missing.');
createRoot(root).render(
  <StrictMode>
    <Page />
  </StrictMode>,
);
