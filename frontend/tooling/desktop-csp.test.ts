import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
import { TRUSTED_TYPES_CSP } from '../apps/web/trusted-types.ts';

// JSON configs cannot carry comments, so this test keeps the spike and local-check CSPs in step with the base CSP:
// the spike may only add LiveKit origins to connect-src, the local check only the AppHost's control plane and cells.

interface TauriConfig {
  app: { security: { csp: string } };
}

const LIVEKIT_ORIGINS = [
  'ws://localhost:7880',
  'http://localhost:7880',
  'wss://*.livekit.cloud',
  'https://*.livekit.cloud',
];

/** The AppHost's fixed ports: the control plane (http) and the two cells' app hosts, with their live connections. */
const APPHOST_ORIGINS = [
  'http://localhost:5100',
  'http://localhost:5200',
  'ws://localhost:5200',
  'http://localhost:5202',
  'ws://localhost:5202',
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

  it('local-check CSP equals the base CSP plus the AppHost origins in connect-src', () => {
    const base = readCsp('tauri.conf.json');
    const local = readCsp('tauri.local.conf.json');

    const expected = new Map(base);
    expected.set('connect-src', [...(base.get('connect-src') ?? []), ...APPHOST_ORIGINS]);

    expect(Object.fromEntries(local)).toEqual(Object.fromEntries(expected));
  });

  it('base CSP blocks frames, plugins, base and form targets', () => {
    const base = readCsp('tauri.conf.json');

    for (const directive of ['frame-src', 'object-src', 'base-uri', 'form-action']) {
      expect(base.get(directive)).toEqual(["'none'"]);
    }
  });

  it('base CSP requires the same Trusted Types as the web build', () => {
    const base = readCsp('tauri.conf.json');

    for (const [name, ...sources] of TRUSTED_TYPES_CSP.split(';').map((directive) =>
      directive.trim().split(/\s+/),
    )) {
      expect(base.get(name ?? '')).toEqual(sources);
    }
  });

  it('base CSP loads images only from the app itself, which is where the emoji images are', () => {
    // No host: an emoji image from anywhere else would be blocked.
    expect(readCsp('tauri.conf.json').get('img-src')).toEqual(["'self'", 'data:', 'blob:']);
  });
});
