// Measures the message list in a real browser: the Profiler numbers of apps/web/e2e/message-list.html for a mouse
// sweep over the rows, the mount, and a wheel scroll. Run from the repository root with the web dev server up:
//   pnpm -C frontend/apps/web exec vite --port 5179   (VITE_RICH_TEXT_ENABLED=true)
//   node load-tests/tools/message-list-profile.mjs [baseUrl] [runs]
// Prints medians; it asserts nothing (timings are for people to read, never a test).
import { createRequire } from 'node:module';

const require = createRequire(new URL('../../frontend/apps/web/package.json', import.meta.url));
const { chromium } = require('@playwright/test');

const baseUrl = process.argv[2] ?? 'http://localhost:5179';
const runs = Number(process.argv[3] ?? 5);
const median = (values) => [...values].sort((a, b) => a - b)[Math.floor(values.length / 2)];

const browser = await chromium.launch();
const results = { sweepCommits: [], sweepMs: [], mountMs: [], nodes: [], scrollCommits: [], scrollMs: [] };
for (let run = 0; run < runs; run += 1) {
  const page = await browser.newPage({ viewport: { width: 1000, height: 700 } });
  await page.goto(`${baseUrl}/e2e/message-list.html`);
  await page.getByRole('feed').waitFor();
  await page.waitForTimeout(500);
  results.mountMs.push(await page.evaluate(() => window.__listProfile.mountDuration));
  results.nodes.push(await page.evaluate(() => document.querySelectorAll('[role="feed"] *').length));

  // (a) the pointer from the top of the list to its bottom in small steps
  const box = await page.getByRole('feed').boundingBox();
  await page.mouse.move(box.x + 300, box.y + 2);
  await page.evaluate(() => window.__listProfile.reset());
  await page.mouse.move(box.x + 300, box.y + box.height - 2, { steps: 60 });
  await page.waitForTimeout(300);
  const sweep = await page.evaluate(() => ({ c: window.__listProfile.commits, d: window.__listProfile.duration }));
  results.sweepCommits.push(sweep.c);
  results.sweepMs.push(sweep.d);

  // (c) wheel scrolling from the end to the start
  await page.mouse.move(box.x + 300, box.y + 200);
  await page.evaluate(() => window.__listProfile.reset());
  for (let step = 0; step < 40; step += 1) {
    await page.mouse.wheel(0, -150);
    await page.waitForTimeout(16);
  }
  await page.waitForTimeout(300);
  const scroll = await page.evaluate(() => ({ c: window.__listProfile.commits, d: window.__listProfile.duration }));
  results.scrollCommits.push(scroll.c);
  results.scrollMs.push(scroll.d);
  await page.close();
}
await browser.close();
for (const [name, values] of Object.entries(results)) {
  console.log(`${name.padEnd(14)} median ${median(values).toFixed(2)}   all ${values.map((v) => v.toFixed(1)).join(' ')}`);
}
