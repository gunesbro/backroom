import { afterEach, describe, expect, it, vi } from 'vitest';
import { CellAddressError, cellOrigin, createMessagingApi } from './api';

afterEach(() => {
  vi.restoreAllMocks();
});

describe('cellOrigin', () => {
  it.each([
    ['https://cell.example.test', 'https://cell.example.test'],
    ['https://cell.example.test:8443/some/path?x=1#y', 'https://cell.example.test:8443'],
  ])('Origin_OfAnHttpsAddress_%s_IsItsOriginOnly', (url, expected) => {
    expect(cellOrigin(url, false)).toBe(expected);
  });

  it.each(['http://localhost:5100', 'http://127.0.0.1:5100', 'http://[::1]:5100'])(
    'Origin_OfLoopbackHttp_%s_IsAllowedOnlyInDevelopment',
    (url) => {
      expect(cellOrigin(url, true)).toBe(url);
      expect(cellOrigin(url, false)).toBeUndefined();
    },
  );

  it.each([
    'http://cell.example.test',
    'http://localhost.example.test',
    'javascript:alert(1)',
    'ftp://cell.example.test',
    'https://user:pass@cell.example.test',
    '//cell.example.test',
    'not a url',
    '',
  ])('Origin_OfAnythingElse_%s_IsRefused', (url) => {
    expect(cellOrigin(url, true)).toBeUndefined();
  });
});

describe('createMessagingApi', () => {
  it('Request_ToARefusedAddress_NeverAsksForATokenOrCallsTheNetwork', async () => {
    const fetchSpy = vi.spyOn(globalThis, 'fetch');
    const cellToken = vi.fn(() => Promise.resolve('cell-token'));
    const api = createMessagingApi('http://cell.example.test', cellToken, { allowLoopbackHttp: false });

    await expect(api.listChannels('org', undefined)).rejects.toBeInstanceOf(CellAddressError);
    expect(cellToken).not.toHaveBeenCalled();
    expect(fetchSpy).not.toHaveBeenCalled();
  });

  it('Request_ToAnAllowedAddress_UsesOnlyItsOrigin', async () => {
    const fetchSpy = vi
      .spyOn(globalThis, 'fetch')
      .mockResolvedValue(Response.json({ channels: [], next: null }));
    const api = createMessagingApi('https://cell.example.test/ignored?x=1', () =>
      Promise.resolve('cell-token'),
    );

    await api.listChannels('org', undefined);

    expect(fetchSpy.mock.calls[0]?.[0]).toBe('https://cell.example.test/api/v1/orgs/org/channels?limit=50');
  });
});
