import type { Hub, HubFactory } from '@backroom/realtime';
import { describe, expect, it, vi } from 'vitest';
import { createRealtimeFactory } from './realtime-context';

const cell = (url: string) => ({ id: 'a', url });

function setup(allowLoopbackHttp: boolean) {
  const cellTokens = vi.fn(() => Promise.resolve('cell-token'));
  const urls: string[] = [];
  const tokens: string[] = [];
  const hubFactory: HubFactory = async (url, getAccessToken) => {
    urls.push(url);
    // A real hub asks for the token on every connection attempt.
    tokens.push(await getAccessToken());
    const hub: Hub = {
      start: () => Promise.resolve(),
      stop: () => Promise.resolve(),
      invoke: () => Promise.resolve(undefined),
      on: () => undefined,
      onClose: () => undefined,
    };
    return hub;
  };
  const factory = createRealtimeFactory(cellTokens, { allowLoopbackHttp, hubFactory });
  return { factory, cellTokens, urls, tokens };
}

describe('createRealtimeFactory', () => {
  it.each([
    'http://cell.example.test',
    'javascript:alert(1)',
    'https://user:pass@cell.example.test',
    'not a url',
  ])('Connect_ToARefusedAddress_%s_NeverAsksForATokenOrOpensAConnection', async (url) => {
    const { factory, cellTokens, urls } = setup(false);
    const client = factory(cell(url));

    const statuses: string[] = [];
    client.onStatus((status) => statuses.push(status));
    client.start();
    // Flush pending promises: an attempt that started would have built the hub by now.
    await Promise.resolve();
    await Promise.resolve();

    expect(cellTokens).not.toHaveBeenCalled();
    expect(urls).toEqual([]);
    // It never starts, so nothing says "connecting" for an address that can never connect.
    expect(client.status()).toBe('stopped');
    expect(statuses).toEqual([]);
    await client.stop();
  });

  it('Connect_ToAnHttpsCell_UsesOnlyItsOriginAndTheCellToken', async () => {
    const { factory, cellTokens, urls, tokens } = setup(false);
    const client = factory(cell('https://cell.example.test/some/path?x=1'));

    client.start();
    await vi.waitFor(() => {
      expect(client.status()).toBe('connected');
    });

    expect(urls).toEqual(['https://cell.example.test/hubs/realtime']);
    expect(cellTokens).toHaveBeenCalledWith('a');
    expect(tokens).toEqual(['cell-token']);
    await client.stop();
  });

  it('Connect_ToLoopbackHttp_IsAllowedOnlyWhereTheBuildAllowsIt', async () => {
    const allowed = setup(true);
    const client = allowed.factory(cell('http://localhost:5200'));
    client.start();
    await vi.waitFor(() => {
      expect(client.status()).toBe('connected');
    });
    expect(allowed.urls).toEqual(['http://localhost:5200/hubs/realtime']);
    await client.stop();

    const refused = setup(false);
    const other = refused.factory(cell('http://localhost:5200'));
    other.start();
    await Promise.resolve();
    await Promise.resolve();
    expect(other.status()).toBe('stopped');
    expect(refused.urls).toEqual([]);
    expect(refused.cellTokens).not.toHaveBeenCalled();
    await other.stop();
  });
});
