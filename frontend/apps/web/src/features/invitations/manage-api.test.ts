import { afterEach, describe, expect, it, vi } from 'vitest';
import { createInvitationsApi } from './manage-api';

afterEach(() => {
  vi.restoreAllMocks();
});

describe('createInvitationsApi', () => {
  it('List_WithoutTheOption_FollowsTheBuildsLoopbackRule', async () => {
    // Tests run as a development build, where the shared rule allows loopback http.
    const fetchSpy = vi
      .spyOn(globalThis, 'fetch')
      .mockResolvedValue(Response.json({ items: [], next: null }));
    const api = createInvitationsApi('http://localhost:5200', () => Promise.resolve('cell-token'));

    await api.list('org', undefined);

    expect(new URL(fetchSpy.mock.calls[0]?.[0] as string).origin).toBe('http://localhost:5200');
  });

  it('List_ToARefusedAddress_NeverAsksForAToken', async () => {
    const fetchSpy = vi.spyOn(globalThis, 'fetch');
    const cellToken = vi.fn(() => Promise.resolve('cell-token'));
    const api = createInvitationsApi('http://cell.example.test', cellToken, { allowLoopbackHttp: false });

    await expect(api.list('org', undefined)).rejects.toThrow();
    expect(cellToken).not.toHaveBeenCalled();
    expect(fetchSpy).not.toHaveBeenCalled();
  });
});
