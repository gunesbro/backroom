// Fails when a built app exceeds its bundle budget (frontend/bundle-budget.json).
// Run after `pnpm build`: `pnpm check:bundle`.
//
// Uses Vite's build manifest (build.manifest: true), so it does not depend on the base path or on
// how index.html is written: initial = entry chunks plus everything they import statically (with
// their CSS); lazy = every dynamically imported chunk (with its CSS). Sizes are gzip, in kB of
// 1000 bytes, like Vite's build report.
import { existsSync, readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { gzipSync } from 'node:zlib';

const root = fileURLToPath(new URL('..', import.meta.url));
const budget = JSON.parse(readFileSync(join(root, 'bundle-budget.json'), 'utf8'));

const round = (value) => Math.round(value * 10) / 10;
const gzipKb = (dist, file) => gzipSync(readFileSync(join(dist, file))).length / 1000;

const problems = [];
const report = [];

const builtApps = readdirSync(join(root, 'apps')).filter((app) =>
  existsSync(join(root, 'apps', app, 'vite.config.ts')),
);
for (const app of builtApps) {
  if (!budget.apps[app]) {
    problems.push(`${app}: no budget in bundle-budget.json`);
  }
}

for (const [app, limits] of Object.entries(budget.apps)) {
  const dist = join(root, 'apps', app, 'dist');
  const manifestPath = join(dist, '.vite', 'manifest.json');
  if (!existsSync(manifestPath)) {
    problems.push(`${app}: ${manifestPath} not found (run the build with build.manifest enabled)`);
    continue;
  }

  const manifest = JSON.parse(readFileSync(manifestPath, 'utf8'));
  const entries = Object.keys(manifest).filter((key) => manifest[key].isEntry);
  if (entries.length === 0) {
    problems.push(`${app}: the manifest has no entry chunk`);
    continue;
  }

  // Static closure of the entries: what the browser loads before the app can render.
  const initialChunks = new Set();
  const visit = (key) => {
    if (initialChunks.has(key)) return;
    initialChunks.add(key);
    for (const imported of manifest[key].imports ?? []) visit(imported);
  };
  entries.forEach(visit);

  const filesOf = (keys, kind) => [
    ...new Set(
      [...keys].flatMap((key) => (kind === 'js' ? [manifest[key].file] : (manifest[key].css ?? []))),
    ),
  ];
  const sum = (files) => round(files.reduce((total, file) => total + gzipKb(dist, file), 0));

  report.push([app, 'initial JS', sum(filesOf(initialChunks, 'js')), limits.initialJs]);
  report.push([app, 'initial CSS', sum(filesOf(initialChunks, 'css')), limits.initialCss]);

  // Each dynamically imported chunk with its own static imports that are not already initial.
  const lazyRoots = Object.keys(manifest).filter(
    (key) => manifest[key].isDynamicEntry && !initialChunks.has(key),
  );
  for (const key of lazyRoots) {
    const closure = new Set();
    const walk = (k) => {
      if (closure.has(k) || initialChunks.has(k)) return;
      closure.add(k);
      for (const imported of manifest[k].imports ?? []) walk(imported);
    };
    walk(key);
    const size = sum([...filesOf(closure, 'js'), ...filesOf(closure, 'css')]);
    report.push([app, `lazy ${manifest[key].file}`, size, limits.lazyChunk]);
  }
}

for (const [app, label, size, limit] of report) {
  const ok = size <= limit;
  if (!ok) problems.push(`${app}: ${label} is ${size} kB, budget ${limit} kB`);
  console.log(
    `${ok ? 'ok  ' : 'FAIL'} ${app.padEnd(4)} ${label.padEnd(48)} ${String(size).padStart(7)} kB / ${limit} kB`,
  );
}

if (problems.length > 0) {
  console.error(`\n${problems.join('\n')}`);
  console.error('\nSplit the code (lazy import), drop the dependency, or justify a new budget in the PR.');
  process.exit(1);
}
