// Shared by update-emoji-flags.js and emoji-flags.test.ts.
import { createHash } from 'node:crypto';
import { readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';

/**
 * A flag as it is vendored. Upstream draws on a 1000 by 1000 canvas with two decimals and absolute coordinates only;
 * whole numbers are far below a pixel at any size an emoji is shown, and halve the files. Nothing else changes.
 */
export function shrinkFlag(svg) {
  const paths = [...svg.matchAll(/\sd="([^"]*)"/g)].map((match) => match[1]).join(' ');
  if (!svg.includes('viewBox="0 0 1000 1000"') || /[a-y]/.test(paths)) {
    throw new Error(
      'A flag is not on the 1000 unit canvas or has relative path commands; rounding is not safe.',
    );
  }
  return `${svg
    .replace(/\sd="([^"]*)"/g, (_, d) => {
      const rounded = d.replace(/-?\d*\.\d+/g, (number) => String(Math.round(Number(number))));
      return ` d="${rounded.replace(/\s+/g, ' ').trim()}"`;
    })
    .replace(/>\s+</g, '><')
    .replaceAll('<defs/>', '')
    .trim()}\n`;
}

/** SHA-256 over the names and contents of the vendored flags, in name order. */
export function flagsChecksum(directory) {
  const hash = createHash('sha256');
  for (const name of readdirSync(directory).sort()) {
    hash.update(`${name}\n`);
    hash.update(readFileSync(join(directory, name)));
  }
  return hash.digest('hex');
}
