// Rewrites the "offered" part of docs/contracts/test-data/picker-emoji.json from the emoji picker's data.
// Run it after changing the emoji packages or apps/web/emoji-data-plugin.ts: `pnpm update:emoji-fixture`.
// The web tests fail while the file and the picker differ; a backend test checks the file against the server's rule.
import { readFileSync, writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { offeredEmojis } from '../apps/web/emoji-data-plugin.ts';

const path = fileURLToPath(new URL('../../docs/contracts/test-data/picker-emoji.json', import.meta.url));
const fixture = JSON.parse(readFileSync(path, 'utf8'));

// One line per group keeps the file and its diffs readable; no emoji contains a space.
fixture.offered = Object.fromEntries(
  Object.entries(offeredEmojis()).map(([group, emojis]) => [group, emojis.join(' ')]),
);

// Invisible characters stay visible as escapes.
const json = JSON.stringify(fixture, null, 2).replaceAll(
  /\u200D|\u20E3|\uFE0F/g,
  (character) => `\\u${character.charCodeAt(0).toString(16).toUpperCase().padStart(4, '0')}`,
);
writeFileSync(path, `${json}\n`);
