// Vendors the country and region flags of the emoji image set into apps/web/emoji-images/noto/flags.
//
// The npm package with the Noto Emoji images has no country flags: upstream keeps them apart, in
// third_party/region-flags/waved-svg of https://github.com/googlefonts/noto-emoji. This script fetches that folder at
// the commit pinned in apps/web/emoji-images/noto/SOURCE.json (git verifies the content against the commit hash),
// makes the files smaller and records a checksum of the result, which tooling/emoji-flags.test.ts checks.
//
// To move to a newer release, change "tag" and "commit" in SOURCE.json and run `pnpm -C frontend update:emoji-flags`
// (needs git and network access).
import { execFileSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { flagsChecksum, shrinkFlag } from './emoji-flags.js';

const target = fileURLToPath(new URL('../apps/web/emoji-images/noto/', import.meta.url));
const sourceFile = join(target, 'SOURCE.json');
const source = JSON.parse(readFileSync(sourceFile, 'utf8'));
const FLAGS = 'third_party/region-flags';

const work = mkdtempSync(join(tmpdir(), 'emoji-flags-'));
const git = (...args) => execFileSync('git', args, { cwd: work, stdio: ['ignore', 'pipe', 'inherit'] });
try {
  // Only the flag folder is downloaded; the whole repository is several hundred megabytes.
  git('init', '--quiet');
  git('remote', 'add', 'origin', source.repository);
  git('config', 'core.symlinks', 'false');
  git('sparse-checkout', 'set', FLAGS);
  git('fetch', '--quiet', '--depth', '1', '--filter=blob:none', 'origin', source.commit);
  git('checkout', '--quiet', 'FETCH_HEAD');
  const head = git('rev-parse', 'HEAD').toString().trim();
  if (head !== source.commit) throw new Error(`Fetched ${head}, expected ${source.commit}`);

  const from = join(work, FLAGS, 'waved-svg');
  const flags = join(target, 'flags');
  rmSync(flags, { recursive: true, force: true });
  mkdirSync(flags, { recursive: true });
  const aliases = {};
  for (const name of readdirSync(from).sort()) {
    const content = readFileSync(join(from, name), 'utf8');
    // A region without its own flag is a symbolic link to another one; without symlinks git writes the target's name.
    if (!content.startsWith('<')) aliases[name] = content.trim();
    else writeFileSync(join(flags, name), shrinkFlag(content));
  }
  const license = readFileSync(join(work, FLAGS, 'LICENSE'), 'utf8');
  writeFileSync(join(flags, 'LICENSE'), license.replaceAll('\r\n', '\n'));
  source.aliases = aliases;
  source.flagsSha256 = flagsChecksum(flags);
  writeFileSync(sourceFile, `${JSON.stringify(source, null, 2)}\n`);
  console.log(
    `${String(readdirSync(flags).length - 1)} flags and ${String(Object.keys(aliases).length)} aliases written.`,
  );
} finally {
  rmSync(work, { recursive: true, force: true });
}
