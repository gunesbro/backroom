import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

// JSON configs cannot carry comments, so this test keeps the spike CSP in step with the base CSP:
// the spike may only add LiveKit origins to connect-src.

interface TauriConfig {
  app: { security: { csp: string } };
}

const LIVEKIT_ORIGINS = [
  'ws://localhost:7880',
  'http://localhost:7880',
  'wss://*.livekit.cloud',
  'https://*.livekit.cloud',
];

function readCsp(file: string): Map<string, string[]> {
  const path = fileURLToPath(new URL(`../apps/desktop/src-tauri/${file}`, import.meta.url));
  const config = JSON.parse(readFileSync(path, 'utf8')) as TauriConfig;
  return new Map(
    config.app.security.csp
      .split(';')
      .map((directive) => directive.trim().split(/\s+/))
      .filter((parts) => parts[0])
      .map(([name, ...sources]) => [name ?? '', sources]),
  );
}

describe('desktop CSP', () => {
  it('spike CSP equals the base CSP plus the LiveKit origins in connect-src', () => {
    const base = readCsp('tauri.conf.json');
    const spike = readCsp('tauri.spike.conf.json');

    const expected = new Map(base);
    expected.set('connect-src', [...(base.get('connect-src') ?? []), ...LIVEKIT_ORIGINS]);

    expect(Object.fromEntries(spike)).toEqual(Object.fromEntries(expected));
  });

  it('base CSP blocks frames, plugins, base and form targets', () => {
    const base = readCsp('tauri.conf.json');

    for (const directive of ['frame-src', 'object-src', 'base-uri', 'form-action']) {
      expect(base.get(directive)).toEqual(["'none'"]);
    }
  });
});
