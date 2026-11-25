// The page of the browser smoke test (apps/web/e2e): the composer alone, with no server. What is sent is drawn as a
// message below it and kept as JSON for the test to read. Nothing imports this file, so no build contains it.
import { StrictMode, useState } from 'react';
import { createRoot } from 'react-dom/client';
import '@fontsource/ibm-plex-sans/400.css';
import '@backroom/ui';
import { LocaleProvider } from '@/shared/locale-provider';
import { Composer } from '../composer';
import type { Block } from '../rich-text/blocks';
import { MessageBody } from '../rich-text/message-blocks';
import '../messaging.css';

type Sent = { text: string; blocks: Block[] };

// eslint-disable-next-line react-refresh/only-export-components -- a test page: the whole page reloads on a change
function Page() {
  const [sent, setSent] = useState<Sent[]>([]);
  return (
    <LocaleProvider locale="en">
      <main style={{ maxWidth: 640, margin: '0 auto', background: 'var(--ph-panel)' }}>
        <h1>Composer smoke test</h1>
        <ol aria-label="Sent messages">
          {sent.map((message, index) => (
            <li key={index} data-blocks={JSON.stringify(message.blocks)}>
              <MessageBody blocks={message.blocks} text={message.text} sending={false} />
            </li>
          ))}
        </ol>
        <Composer
          channelName="general"
          onSend={(text, blocks) => {
            setSent((current) => [...current, { text, blocks: blocks ?? [] }]);
          }}
        />
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
